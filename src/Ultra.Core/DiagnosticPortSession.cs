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

    public DiagnosticPortSession(int pid, bool sampler, string baseName, CancellationToken token)
    {
        _pid = pid;
        _sampler = sampler;
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
                _cancelConnectSource.CancelAfter(5000);
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
                if (task.IsFaulted)
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
                        ClrTraceEventParser.Keywords.Stack |
                        ClrTraceEventParser.Keywords.StartEnumeration
                    );
                }

                var ultraEventProvider = new EventPipeProvider(providerName, level, (long)keywords);
                var config = new EventPipeSessionConfiguration([ultraEventProvider], 512, !_sampler, true);
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
            if (_profilingTask is null)
            {
                // We cancel any pending connection
                await _cancelConnectSource.CancelAsync();
            }
            else
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

                        // If the target process exits before it observes StopAsync, the runtime can close the
                        // EventPipe stream at a block boundary without the final EndOfStream block. TraceEvent
                        // treats that as a truncated file, so repair that narrow, lossless case before conversion.
                        if (_nettraceFilePath is not null)
                        {
                            TryAppendMissingV6EndOfStreamBlock(_nettraceFilePath);
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

    internal static bool TryAppendMissingV6EndOfStreamBlock(string nettraceFilePath)
    {
        using var stream = new FileStream(nettraceFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096);
        return TryAppendMissingV6EndOfStreamBlock(stream);
    }

    internal static bool TryAppendMissingV6EndOfStreamBlock(Stream stream)
    {
        const int nettraceV6HeaderLength = 20;
        const int blockHeaderLength = 4;
        const int endOfStreamBlockKind = 0;

        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek || stream.Length < nettraceV6HeaderLength)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[nettraceV6HeaderLength];
        stream.Position = 0;
        if (stream.Read(header) != header.Length || !header[..8].SequenceEqual("Nettrace"u8))
        {
            return false;
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(header[8..12]) != 0)
        {
            return false;
        }

        var majorVersion = BinaryPrimitives.ReadInt32LittleEndian(header[12..16]);
        if (majorVersion != 6)
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

            sawBlock = true;
            var blockHeader = BinaryPrimitives.ReadInt32LittleEndian(blockHeaderBytes);
            var blockKind = (int)((uint)blockHeader >> 24);
            var blockLength = blockHeader & 0x00FF_FFFF;
            offset += blockHeaderLength;

            if (blockKind == endOfStreamBlockKind)
            {
                return true;
            }

            if (offset + blockLength > fileLength)
            {
                return false;
            }

            offset += blockLength;
        }

        if (!sawBlock || offset != fileLength)
        {
            return false;
        }

        stream.Position = fileLength;
        blockHeaderBytes.Clear();
        stream.Write(blockHeaderBytes);
        return true;
    }
}
