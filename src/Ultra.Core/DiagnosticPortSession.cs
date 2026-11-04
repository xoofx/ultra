// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;
using Ultra.Sampler;

namespace Ultra.Core;

/// <summary>
/// Handles an EventPipe session to a diagnostic port.
/// </summary>
internal class DiagnosticPortSession
{
    private readonly int _pid;
    private readonly bool _sampler;
    private readonly bool _lockContention;
    private readonly string _baseName;
    private readonly Task _connectTask;
    private readonly SemaphoreSlim _semaphoreSlim;
    private Task? _profilingTask;
    private readonly CancellationTokenSource _cancelConnectSource;
    private DiagnosticsClient? _diagnosticsClient;
    private EventPipeSession? _eventPipeSession;
    private string? _nettraceFilePath;
    private FileStream? _nettraceFileStream;
    private Task? _eventStreamCopyTask;
    private bool _disposed;

    /// <summary>
    /// How long to wait for the injected sampler to open its diagnostic port socket (it opens it within milliseconds
    /// normally). Only changed by tests, to make the connection fail.
    /// </summary>
    internal static TimeSpan SamplerConnectTimeout = TimeSpan.FromSeconds(5);

    // lockContention: also the contention events in the CLR session (--locks)
    public DiagnosticPortSession(int pid, bool sampler, string baseName, CancellationToken token, bool lockContention = false)
    {
        _pid = pid;
        _sampler = sampler;
        _lockContention = lockContention;
        _baseName = baseName;
        _cancelConnectSource = new CancellationTokenSource();
        _semaphoreSlim = new SemaphoreSlim(1);
        _connectTask = ConnectAndStartProfilingImpl(token);
    }

    public bool TryGetNettraceFilePathIfExists([NotNullWhen(true)] out string? nettraceFilePath)
    {
        if (_nettraceFilePath is null || !File.Exists(_nettraceFilePath))
        {
            nettraceFilePath = null;
            return false;
        }

        nettraceFilePath = _nettraceFilePath;
        return nettraceFilePath is not null;
    }

    private async Task ConnectAndStartProfilingImpl(CancellationToken token)
    {
        CancellationTokenSource linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, _cancelConnectSource.Token);
        try
        {

            var connectCancellationToken = linkedCancellationTokenSource.Token;

            if (_sampler)
            {
                // Give enough time to the injected sampler (NativeAOT) to boot and create the diagnostic port socket
                _cancelConnectSource.CancelAfter(SamplerConnectTimeout);
            }

            var connectionAddress = await TryFindConnectionAddress(_pid, _sampler, connectCancellationToken).ConfigureAwait(false);
            if (connectionAddress is null) return;

            _diagnosticsClient = (await DiagnosticsClientConnector.FromDiagnosticPort(connectionAddress, connectCancellationToken).ConfigureAwait(false))?.Instance;
            if (_diagnosticsClient is null) return;

            await _diagnosticsClient.WaitForConnectionAsync(connectCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            if (_sampler && _cancelConnectSource is not null && _cancelConnectSource.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Cannot connect to the diagnostic port socket for pid {_pid}", ex);
            }
            return;
        }
        finally
        {
            linkedCancellationTokenSource.Dispose();
        }
    }

    public async Task StartProfiling(CancellationToken token)
    {
        // We want to make sure that we are not disposing while we are starting a session
        await _semaphoreSlim.WaitAsync(token);

        try
        {
            if (_disposed)
            {
                return;
            }

            // Note the Unwrap: awaiting _profilingTask must wait for the async continuation itself
            // (the EventPipe session creation), not only for its synchronous prefix.
            _profilingTask = _connectTask.ContinueWith(async task =>
            {
                // No connection (faulted, or cancelled at stop because the process has no diagnostic port): no session
                if (task.IsFaulted || _diagnosticsClient is null)
                {
                    return;
                }

                _nettraceFilePath = Path.Combine(Environment.CurrentDirectory, $"{_baseName}_{_pid}_{(_sampler ? "sampler" : "clr")}.nettrace");
                _nettraceFileStream = new FileStream(_nettraceFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);

                long keywords = -1;
                var providerName = UltraSamplerConstants.ProviderName;
                var level = EventLevel.Verbose;

                if (!_sampler)
                {
                    providerName = ClrTraceEventParser.ProviderName;
                    keywords = (long)(
                        ClrTraceEventParser.Keywords.JITSymbols |
                        ClrTraceEventParser.Keywords.Exception |
                        ClrTraceEventParser.Keywords.GC |
                        ClrTraceEventParser.Keywords.GCHeapAndTypeNames |
                        ClrTraceEventParser.Keywords.Interop |
                        ClrTraceEventParser.Keywords.JITSymbols |
                        ClrTraceEventParser.Keywords.Jit |
                        ClrTraceEventParser.Keywords.JittedMethodILToNativeMap |
                        ClrTraceEventParser.Keywords.Loader |
                        ClrTraceEventParser.Keywords.StartEnumeration
                    );

                    if (_lockContention)
                    {
                        keywords |= (long)ClrTraceEventParser.Keywords.Contention;
                    }
                }

                // The CLR session asks for no stack per event (nor for the Stack keyword's ClrStackWalk events), as on Windows (#32): the
                // runtime would walk the managed stack of every CLR event (allocation ticks, GC, JIT, loader...) and nothing reads them,
                // the CPU samples have their own stacks. Only --locks asks for them, for the stacks of the lock waits.
                // The macOS sampler session keeps them: its events carry their own call stacks, so turning them off there too may
                // lower the overhead, not tried on a Mac yet (see the macOS remark in readme.md).
                var requestStackwalk = _sampler || _lockContention;
                var ultraEventProvider = new EventPipeProvider(providerName, level, (long)keywords);
                var config = new EventPipeSessionConfiguration([ultraEventProvider], 512, !_sampler, requestStackwalk);
                _eventPipeSession = await _diagnosticsClient!.StartEventPipeSessionAsync(config, token).ConfigureAwait(false);
                _eventStreamCopyTask = _eventPipeSession.EventStream.CopyToAsync(_nettraceFileStream, token);
            }, token).Unwrap();
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    public long GetNettraceFileLength() => _nettraceFileStream?.Length ?? 0;

    public async Task WaitForConnect()
    {
        await _connectTask.ConfigureAwait(false);
    }

    private static async Task<string?> TryFindConnectionAddress(int pid, bool sampler, CancellationToken token)
    {
        var tempFolder = Path.GetTempPath();
        tempFolder = sampler ? Path.Combine(tempFolder, ".ultra") : tempFolder;

        var pattern = $"dotnet-diagnostic-{pid}-*-socket";
        string? diagnosticPortSocket = null;

        int waitForNextCheckDelayInMs = 10;

        // This loop is blocking until a file is available or the token is cancelled
        while (true)
        {
            if (Directory.Exists(tempFolder))
            {
                DateTime lastWriteTime = default;
                foreach (var file in Directory.EnumerateFiles(tempFolder, pattern))
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTime > lastWriteTime)
                    {
                        diagnosticPortSocket = file;
                        lastWriteTime = fileInfo.LastWriteTime;
                    }
                }

                if (diagnosticPortSocket != null)
                {
                    // Force connect mode
                    diagnosticPortSocket = $"{diagnosticPortSocket},connect";
                    break;
                }
            }

            await Task.Delay(waitForNextCheckDelayInMs, token).ConfigureAwait(false);

            // Let's increase the delay after each check to lower the overhead
            waitForNextCheckDelayInMs += 10;
            waitForNextCheckDelayInMs = Math.Min(waitForNextCheckDelayInMs, 100);
        }

        return diagnosticPortSocket;
    }

    public async ValueTask StopAndDisposeAsync()
    {
        // We want to make sure that we are not disposing while we are connecting/trying to start a session
        // (This could happen if we are trying to profile a non .NET process with the CLR provider)
        await _semaphoreSlim.WaitAsync(CancellationToken.None);

        try
        {
            // We cancel any pending connection (a non .NET process never opens a diagnostic port: without this
            // the stop would wait for it forever)
            if (_profilingTask is null || !_connectTask.IsCompleted)
            {
                await _cancelConnectSource.CancelAsync();
            }

            if (_profilingTask is not null)
            {
                try
                {
                    await _connectTask.ConfigureAwait(false);

                    // We wait for the session to be started (we will stop it right after below)
                    await _profilingTask.ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }

                // Request the session to stop first: the runtime then emits the rundown events (for the CLR session)
                // and closes the event stream, which allows the copy below to complete while the profiled
                // process is still running. The stream must be drained concurrently for the stop to complete.
                Task? stopTask = null;
                if (_eventPipeSession is not null)
                {
                    try
                    {
                        stopTask = _eventPipeSession.StopAsync(CancellationToken.None);
                    }
                    catch
                    {
                        // Ignore
                    }
                }

                if (_eventStreamCopyTask is not null)
                {
                    try
                    {
                        await _eventStreamCopyTask.ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore
                    }
                }

                if (stopTask is not null)
                {
                    try
                    {
                        await stopTask.ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore
                    }
                }

                if (_nettraceFileStream is not null)
                {
                    try
                    {
                        await _nettraceFileStream.DisposeAsync().ConfigureAwait(false);
                        _nettraceFileStream = null;

                        // If the target process exits before it observes StopAsync, the runtime closes the
                        // EventPipe stream without the final EndOfStream block, at a block boundary or in the middle
                        // of a block it was writing (seen on slow CI machines). TraceEvent fails on such a truncated
                        // file ("Read past end of stream"), so drop the partial trailing block, if any, and end the
                        // stream after the last complete block before conversion.
                        if (_nettraceFilePath is not null)
                        {
                            TryAppendMissingEndOfStreamBlock(_nettraceFilePath);
                        }
                    }
                    catch
                    {
                        // Ignore
                    }
                }

                if (_eventPipeSession is not null)
                {
                    try
                    {
                        _eventPipeSession.Dispose();
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }
        }
        finally
        {
            _disposed = true;
            _semaphoreSlim.Release();

            _cancelConnectSource.Dispose();
        }
    }

    internal static bool TryAppendMissingEndOfStreamBlock(string nettraceFilePath)
    {
        using var stream = new FileStream(nettraceFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096);
        return TryAppendMissingEndOfStreamBlock(stream);
    }

    internal static bool TryAppendMissingEndOfStreamBlock(Stream stream)
    {
        const int nettraceMagicLength = 8;
        const int int32Length = 4;

        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek || stream.Length < nettraceMagicLength + int32Length)
        {
            return false;
        }

        Span<byte> nettraceMagic = stackalloc byte[nettraceMagicLength];
        stream.Position = 0;
        if (stream.Read(nettraceMagic) != nettraceMagic.Length || !nettraceMagic.SequenceEqual("Nettrace"u8))
        {
            return false;
        }

        Span<byte> int32Bytes = stackalloc byte[int32Length];
        if (stream.Read(int32Bytes) != int32Bytes.Length)
        {
            return false;
        }

        var reservedOrFastSerializationMagicLength = BinaryPrimitives.ReadInt32LittleEndian(int32Bytes);
        return reservedOrFastSerializationMagicLength == 0
            ? TryAppendMissingV6EndOfStreamBlock(stream)
            : TryAppendMissingFastSerializationEndOfStreamMarker(stream, reservedOrFastSerializationMagicLength);
    }

    private static bool TryAppendMissingV6EndOfStreamBlock(Stream stream)
    {
        const int nettraceV6HeaderLength = 20;
        const int blockHeaderLength = 4;
        const int endOfStreamBlockKind = 0;

        if (stream.Length < nettraceV6HeaderLength)
        {
            return false;
        }

        Span<byte> int32Bytes = stackalloc byte[blockHeaderLength];
        if (stream.Read(int32Bytes) != int32Bytes.Length)
        {
            return false;
        }

        var majorVersion = BinaryPrimitives.ReadInt32LittleEndian(int32Bytes);
        if (majorVersion != 6)
        {
            return false;
        }

        if (stream.Read(int32Bytes) != int32Bytes.Length)
        {
            return false;
        }

        var fileLength = stream.Length;
        var offset = (long)nettraceV6HeaderLength;
        var sawBlock = false;
        Span<byte> blockHeaderBytes = stackalloc byte[blockHeaderLength];
        while (offset + blockHeaderLength <= fileLength)
        {
            stream.Position = offset;
            if (stream.Read(blockHeaderBytes) != blockHeaderBytes.Length)
            {
                return false;
            }

            var blockHeader = BinaryPrimitives.ReadInt32LittleEndian(blockHeaderBytes);
            var blockKind = (int)((uint)blockHeader >> 24);
            var blockLength = blockHeader & 0x00FF_FFFF;

            if (blockKind == endOfStreamBlockKind)
            {
                return true;
            }

            if (offset + blockHeaderLength + blockLength > fileLength)
            {
                // Truncated block: dropped below
                break;
            }

            sawBlock = true;
            offset += blockHeaderLength + blockLength;
        }

        if (!sawBlock)
        {
            return false;
        }

        // Drop a partial trailing block (or block header), if any, then end the stream after the last complete block
        stream.SetLength(offset);
        stream.Position = offset;
        blockHeaderBytes.Clear();
        stream.Write(blockHeaderBytes);
        return true;
    }

    private static bool TryAppendMissingFastSerializationEndOfStreamMarker(Stream stream, int fastSerializationMagicLength)
    {
        const byte nullReferenceTag = 1;
        const byte beginPrivateObjectTag = 5;
        const byte endObjectTag = 6;
        const int int32Length = 4;

        if (fastSerializationMagicLength <= 0 || fastSerializationMagicLength > "!FastSerialization.1"u8.Length)
        {
            return false;
        }

        if (stream.Length < 12L + fastSerializationMagicLength)
        {
            return false;
        }

        Span<byte> fastSerializationMagic = stackalloc byte[fastSerializationMagicLength];
        if (stream.Read(fastSerializationMagic) != fastSerializationMagic.Length || !fastSerializationMagic.SequenceEqual("!FastSerialization.1"u8))
        {
            return false;
        }

        var fileLength = stream.Length;
        var offset = 12L + fastSerializationMagicLength;
        var sawBlock = false;
        Span<byte> int32Bytes = stackalloc byte[int32Length];
        var maximumBlockNameLength = "Microsoft.DotNet.Runtime.EventPipeFile"u8.Length;
        Span<byte> typeNameBuffer = stackalloc byte[maximumBlockNameLength];

        // offset is always the end of the last complete object: a partial trailing object is dropped below
        while (offset < fileLength)
        {
            stream.Position = offset;
            var objectOffset = offset;
            var tag = stream.ReadByte();
            if (tag < 0)
            {
                return false;
            }

            objectOffset++;
            if (tag == nullReferenceTag)
            {
                return true;
            }

            if (tag != beginPrivateObjectTag)
            {
                return false;
            }

            var status = ReadExpectedByte(stream, beginPrivateObjectTag, ref objectOffset);
            status = status == ReadStatus.Ok ? ReadExpectedByte(stream, nullReferenceTag, ref objectOffset) : status;
            var version = 0;
            var typeNameLength = 0;
            status = status == ReadStatus.Ok ? ReadInt32(stream, int32Bytes, ref objectOffset, out version) : status;
            status = status == ReadStatus.Ok ? ReadInt32(stream, int32Bytes, ref objectOffset, out _) : status;
            status = status == ReadStatus.Ok ? ReadInt32(stream, int32Bytes, ref objectOffset, out typeNameLength) : status;
            if (status == ReadStatus.Invalid)
            {
                return false;
            }

            if (status == ReadStatus.Truncated)
            {
                break;
            }

            if (typeNameLength <= 0 || typeNameLength > maximumBlockNameLength)
            {
                return false;
            }

            var typeName = typeNameBuffer[..typeNameLength];
            if (stream.ReadAtLeast(typeName, typeName.Length, throwOnEndOfStream: false) != typeName.Length)
            {
                break;
            }

            objectOffset += typeNameLength;
            status = ReadExpectedByte(stream, endObjectTag, ref objectOffset);
            if (status == ReadStatus.Invalid)
            {
                return false;
            }

            if (status == ReadStatus.Truncated)
            {
                break;
            }

            int blockSize;
            if (typeName.SequenceEqual("Trace"u8) || typeName.SequenceEqual("Microsoft.DotNet.Runtime.EventPipeFile"u8))
            {
                if (version > 4)
                {
                    return false;
                }

                blockSize = version >= 3 ? 48 : 32;
            }
            else if (typeName.SequenceEqual("MetadataBlock"u8) ||
                     typeName.SequenceEqual("StackBlock"u8) ||
                     typeName.SequenceEqual("EventBlock"u8) ||
                     typeName.SequenceEqual("SPBlock"u8))
            {
                if (version > 2)
                {
                    return false;
                }

                if (ReadInt32(stream, int32Bytes, ref objectOffset, out blockSize) == ReadStatus.Truncated)
                {
                    break;
                }

                if (blockSize < 0)
                {
                    return false;
                }

                objectOffset += (4 - ((int)objectOffset & 0x3)) & 0x3;
            }
            else
            {
                return false;
            }

            // The block payload and the end object tag
            if (objectOffset + blockSize + 1 > fileLength)
            {
                break;
            }

            objectOffset += blockSize;
            stream.Position = objectOffset;
            if (ReadExpectedByte(stream, endObjectTag, ref objectOffset) != ReadStatus.Ok)
            {
                return false;
            }

            offset = objectOffset;
            sawBlock = true;
        }

        if (!sawBlock)
        {
            return false;
        }

        // Drop a partial trailing object, if any, then end the stream after the last complete object
        stream.SetLength(offset);
        stream.Position = offset;
        stream.WriteByte(nullReferenceTag);
        return true;
    }

    private static ReadStatus ReadInt32(Stream stream, Span<byte> buffer, ref long offset, out int value)
    {
        if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length)
        {
            value = 0;
            return ReadStatus.Truncated;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        offset += buffer.Length;
        return ReadStatus.Ok;
    }

    private static ReadStatus ReadExpectedByte(Stream stream, byte expected, ref long offset)
    {
        var value = stream.ReadByte();
        if (value < 0)
        {
            return ReadStatus.Truncated;
        }

        if (value != expected)
        {
            return ReadStatus.Invalid;
        }

        offset++;
        return ReadStatus.Ok;
    }

    private enum ReadStatus
    {
        Ok,
        Truncated,
        Invalid,
    }
}
