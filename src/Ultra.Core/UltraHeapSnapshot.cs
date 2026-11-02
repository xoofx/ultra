// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Diagnostics.Tracing;
using ByteSizeLib;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Ultra.Core;

/// <summary>
/// Takes a GC heap snapshot of a .NET process (as dotnet-gcdump does): an EventPipe session with the GC heap snapshot keywords
/// makes the runtime run one induced, blocking gen2 collection that reports every live object (address, size, type), the
/// references between them and the roots. The events are written as is to a <c>_heap.nettrace</c> file, converted later by
/// <see cref="UltraHeapSnapshotConverter"/>.
/// </summary>
/// <remarks>
/// It works with the diagnostic port of the process (a named pipe on Windows, a socket on Linux and macOS), so for .NET Core 3.0+
/// only, and without elevation on the same user. The managed threads of the process are stopped for the whole heap walk: seconds
/// for a heap of tens of millions of objects.
/// </remarks>
internal static class UltraHeapSnapshot
{
    /// <summary>The postfix of the heap snapshot files (after the base name and the pid).</summary>
    public const string FilePostfix = "_heap.nettrace";

    /// <summary>
    /// Takes the snapshot of a process into <paramref name="nettraceFile"/>.
    /// </summary>
    /// <returns>True when the file has a complete snapshot; false (with a warning logged, the file deleted) otherwise.</returns>
    public static async Task<bool> TakeAsync(Process process, string nettraceFile, UltraProfilerOptions options, CancellationToken token)
    {
        var name = $"{process.ProcessName} ({process.Id})";
        void Warn(string text) => (options.LogWarning ?? options.LogProgress)?.Invoke($"Warning: {text}");

        if (process.HasExited)
        {
            Warn($"no GC heap snapshot of {name}: the process has exited");
            return false;
        }

        var (bufferMB, bufferWhy) = BufferSize(process, options);
        options.LogProgress?.Invoke($"Taking a GC heap snapshot of {name} (its threads are stopped during the heap walk), EventPipe buffer {bufferMB:N0} MB ({bufferWhy})");
        var clock = Stopwatch.StartNew();
        EventPipeSession session;
        try
        {
            var client = new DiagnosticsClient(process.Id);
            var provider = new EventPipeProvider(ClrTraceEventParser.ProviderName, EventLevel.Verbose, (long)ClrTraceEventParser.Keywords.GCHeapSnapshot);
            session = await client.StartEventPipeSessionAsync([provider], requestRundown: false, circularBufferMB: bufferMB, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"no GC heap snapshot of {name}: cannot open its diagnostic port ({ex.Message}); a .NET Core 3.0+ process is required");
            return false;
        }

        long objects = 0;
        int lostEvents;
        bool complete = false;
        using (session)
        {
            // The events are copied to the file as fast as they come: a reader slower than the heap walk lets the buffer in the process
            // fill up, and the runtime then drops events. The file is parsed behind the copy, only to know when the heap walk ends.
            // Unbuffered (the copy writes whole chunks): what arrived is in the file at once, a buffer would keep the end of the heap walk
            // from the parser until more events come, which can be a minute
            var copied = new CountingStream(new FileStream(nettraceFile, FileMode.Create, FileAccess.Write, FileShare.Read, 0));
            int copyDone = 0;
            var copy = Task.Run(async () =>
            {
                try
                {
                    await session.EventStream.CopyToAsync(copied, 1 << 20, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    await copied.DisposeAsync().ConfigureAwait(false);
                    Volatile.Write(ref copyDone, 1);
                }
            });
            using var source = new EventPipeEventSource(new FollowingFileStream(nettraceFile, () => Volatile.Read(ref copyDone) == 1));

            // The dump is the gen2 blocking collection that reports objects: the first one that ends with objects is ours
            int dumpGc = -1;
            long objectsInGc = 0;
            var dumped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Clr.GCStart += e =>
            {
                if (dumpGc < 0 && e.Depth == 2 && e.Type != GCType.BackgroundGC)
                {
                    dumpGc = e.Count;
                    objectsInGc = 0;
                }
            };
            source.Clr.GCBulkNode += e =>
            {
                objectsInGc += e.Count;
                Interlocked.Exchange(ref objects, objectsInGc);
            };
            source.Clr.GCStop += e =>
            {
                if (e.Count != dumpGc) return;
                if (objectsInGc > 0) dumped.TrySetResult();
                else dumpGc = -1;
            };

            var processing = Task.Factory.StartNew(() => source.Process(), TaskCreationOptions.LongRunning);
            var timeout = Task.Delay(TimeSpan.FromSeconds(options.HeapSnapshotTimeoutInSeconds), token);
            var lastLog = clock.Elapsed;
            while (true)
            {
                var finished = await Task.WhenAny(dumped.Task, processing, timeout, Task.Delay(options.CheckDeltaTimeInMs, CancellationToken.None)).ConfigureAwait(false);
                if (finished == dumped.Task || finished == processing || finished == timeout || process.HasExited) break;
                if ((clock.Elapsed - lastLog).TotalMilliseconds > options.UpdateLogAfterInMs)
                {
                    options.LogStepProgress?.Invoke($"GC heap snapshot of {name} - {(int)clock.Elapsed.TotalSeconds}s - {Interlocked.Read(ref objects):N0} objects - {ByteSize.FromBytes(copied.BytesWritten)}");
                    lastLog = clock.Elapsed;
                }
            }

            complete = dumped.Task.IsCompleted;
            if (!copy.IsCompleted)
            {
                // The stream must be drained while the session stops
                try
                {
                    await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The process may have exited
                }
            }

            try
            {
                await copy.ConfigureAwait(false);
            }
            catch
            {
                // The process may have exited
            }

            try
            {
                await processing.ConfigureAwait(false);
            }
            catch
            {
                // A truncated stream (the process exited): the snapshot is incomplete
            }

            lostEvents = source.EventsLost;
        }

        if (!complete)
        {
            Warn($"no GC heap snapshot of {name}: {(token.IsCancellationRequested ? "cancelled" : process.HasExited ? "the process exited" : $"the heap walk did not end within {options.HeapSnapshotTimeoutInSeconds}s")}");
            TryDelete(nettraceFile);
            return false;
        }

        if (lostEvents > 0)
        {
            Warn($"the GC heap snapshot of {name} lost {lostEvents:N0} events (buffer of {bufferMB:N0} MB): some objects or references are missing; a bigger --heap-snapshot-buffer may help");
        }

        options.LogProgress?.Invoke($"GC heap snapshot of {name}: {Interlocked.Read(ref objects):N0} objects in {clock.Elapsed.TotalSeconds:0.0}s - {ByteSize.FromBytes(new FileInfo(nettraceFile).Length)} - {lostEvents:N0} events lost (EventPipe buffer {bufferMB:N0} MB)");
        return true;
    }

    /// <summary>
    /// The EventPipe buffer of the snapshot, in MB: <see cref="UltraProfilerOptions.HeapSnapshotBufferInMB"/> when given, else the
    /// private memory of the process (the events of a snapshot are about the size of the GC heap, so this holds them all even if
    /// ultra could not read them as they come), at least 256 MB and at most half the free memory of the machine.
    /// </summary>
    internal static (int MB, string Why) BufferSize(Process process, UltraProfilerOptions options)
    {
        if (options.HeapSnapshotBufferInMB > 0) return (options.HeapSnapshotBufferInMB, "--heap-snapshot-buffer");

        const long MB = 1024 * 1024;
        long processBytes;
        try
        {
            process.Refresh();
            processBytes = process.PrivateMemorySize64 > 0 ? process.PrivateMemorySize64 : process.WorkingSet64;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            processBytes = 0;
        }

        var memory = GC.GetGCMemoryInfo();
        var freeBytes = Math.Max(0, memory.TotalAvailableMemoryBytes - memory.MemoryLoadBytes);
        var size = Math.Max(256, processBytes / MB);
        var cap = Math.Max(256, freeBytes / 2 / MB);
        return size <= cap
            ? ((int)Math.Min(size, int.MaxValue), $"the private memory of the process, {processBytes / MB:N0} MB")
            : ((int)Math.Min(cap, int.MaxValue), $"half the free memory of the machine, {freeBytes / MB:N0} MB free; the process uses {processBytes / MB:N0} MB");
    }

    /// <summary>The pid of a heap snapshot file named <c>..._{pid}_heap.nettrace</c>, or null.</summary>
    public static int? GetProcessId(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (!name.EndsWith(FilePostfix, StringComparison.OrdinalIgnoreCase)) return null;
        var stem = name[..^FilePostfix.Length];
        var underscore = stem.LastIndexOf('_');
        return underscore >= 0 && int.TryParse(stem.AsSpan(underscore + 1), out var pid) ? pid : null;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // Ignore
        }
    }

    /// <summary>A write-only stream that counts the bytes written to another one (read from another thread).</summary>
    private sealed class CountingStream(Stream output) : Stream
    {
        private long _bytesWritten;

        public long BytesWritten => Interlocked.Read(ref _bytesWritten);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            output.Write(buffer);
            Interlocked.Add(ref _bytesWritten, buffer.Length);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _bytesWritten, buffer.Length);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) output.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await output.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads a file while it is written: at its current end, waits for more until <paramref name="writerDone"/> (then the end is the end).
    /// </summary>
    private sealed class FollowingFileStream(string path, Func<bool> writerDone) : Stream
    {
        private readonly FileStream _file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (true)
            {
                var read = _file.Read(buffer);
                if (read > 0) return read;
                // Done: what the writer flushed before saying so is all there is
                if (writerDone()) return _file.Read(buffer);
                Thread.Sleep(2);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file.Dispose();
            base.Dispose(disposing);
        }
    }
}
