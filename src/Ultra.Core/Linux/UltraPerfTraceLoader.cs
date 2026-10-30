// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Ultra.Core.Model;
using XenoAtom.Collections;

namespace Ultra.Core.Linux;

/// <summary>
/// Loads a .ultraperf file into a trace session: processes, threads, native modules, samples with their call stacks,
/// and the kernel pseudo module. The managed methods of a process must be loaded in the session (from its CLR
/// nettrace) before this runs, as a managed address is resolved at the time of the sample.
/// </summary>
internal sealed class UltraPerfTraceLoader : IDisposable
{
    private readonly UltraPerfFileReader _reader;
    private readonly Dictionary<int, string> _eventNames = new();

    public UltraPerfTraceLoader(string path)
    {
        _reader = new UltraPerfFileReader(path);
    }

    public UltraPerfHeader Header => _reader.Header;

    /// <summary>The UTC time of the start of the trace (the zero of the relative sample times).</summary>
    public DateTime StartTimeUtc => new(Header.StartUtcTicks, DateTimeKind.Utc);

    /// <summary>The content of /proc/kallsyms recorded in the trace, if any.</summary>
    public string? Kallsyms { get; private set; }

    /// <summary>The kernel symbols, if the trace has them.</summary>
    public KernelSymbolResolver? KernelSymbols { get; private set; }

    /// <summary>The number of records the kernel reported as lost.</summary>
    public ulong LostRecords { get; private set; }

    /// <summary>
    /// Loads the processes of the given ids into the session.
    /// </summary>
    /// <param name="session">The session to fill.</param>
    /// <param name="processIds">The processes to load.</param>
    /// <param name="getClrTimeOffsetInMs">The offset (in ms) between the CLR session start of a process and the start of this trace, used to look up the managed methods (recorded in CLR relative time) at the time of a sample.</param>
    /// <param name="logProgress">Receives "Loading perf events NN%" as the file is read.</param>
    public void Load(UTraceSession session, IReadOnlyCollection<int> processIds, Func<int, double> getClrTimeOffsetInMs, Action<string>? logProgress = null)
    {
        var header = Header;
        if (header.SampleType != PerfEventCollector.SampleType)
        {
            throw new InvalidDataException($"Unsupported perf sample layout 0x{header.SampleType:x} (expected 0x{PerfEventCollector.SampleType:x})");
        }

        session.NumberOfProcessors = header.CpuCount;
        session.StartTime = StartTimeUtc.ToLocalTime();

        var states = new Dictionary<int, ProcessState>();
        foreach (var pid in processIds)
        {
            var process = session.Processes.FirstOrDefault(x => x.ProcessID == pid);
            if (process is null)
            {
                process = new UTraceProcess { ProcessID = pid };
                session.Processes.Add(process);
            }
            process.ProcessArchitecture = header.ProcessArchitecture ?? process.ProcessArchitecture;
            process.RuntimeIdentifier = header.ProcessArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => "linux"
            };
            process.OSDescription = "Linux";
            process.StartTime = session.StartTime;
            states[pid] = new ProcessState(process, getClrTimeOffsetInMs(pid));
        }

        var buildIds = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        ulong lastTimeNs = header.StartMonotonicNs;
        ulong endTimeNs = 0;
        var length = Math.Max(1, _reader.Length);
        int lastPercent = -1;
        long frames = 0;

        foreach (var frame in _reader.ReadFrames())
        {
            var payload = frame.Payload.Span;
            switch (frame.Kind)
            {
                case UltraPerfFrameKind.PerfRecord:
                    ProcessRecord(payload, frame.EventIndex, states, ref lastTimeNs);
                    break;

                case UltraPerfFrameKind.EventName:
                    var (eventIndex, eventName) = UltraPerfPayload.DecodeEventName(payload);
                    _eventNames[eventIndex] = eventName;
                    break;

                case UltraPerfFrameKind.MapSnapshot:
                    var map = UltraPerfPayload.DecodeMap(payload);
                    if (states.TryGetValue(map.Pid, out var mapState))
                    {
                        mapState.AddModule(map.Start, map.End - map.Start, map.Offset, map.Path, default);
                    }
                    break;

                case UltraPerfFrameKind.CommSnapshot:
                    var comm = UltraPerfPayload.DecodeComm(payload);
                    if (states.TryGetValue(comm.Pid, out var commState))
                    {
                        commState.SetName((ulong)comm.Tid, comm.Name);
                    }
                    break;

                case UltraPerfFrameKind.Kallsyms:
                    Kallsyms = Encoding.UTF8.GetString(payload);
                    KernelSymbols = KernelSymbolResolver.Parse(Kallsyms);
                    break;

                case UltraPerfFrameKind.BuildId:
                    var buildId = UltraPerfPayload.DecodeBuildId(payload);
                    buildIds[buildId.Path] = buildId.Id;
                    break;

                case UltraPerfFrameKind.End:
                    var end = UltraPerfPayload.DecodeEnd(payload);
                    LostRecords = end.LostRecords;
                    endTimeNs = end.EndMonotonicNs;
                    break;
            }

            // every 2% of the file (checked every 4096 frames, the position is a property of the stream)
            if (logProgress is not null && (++frames & 4095) == 0)
            {
                var percent = (int)(_reader.Position * 100 / length);
                if (percent >= lastPercent + 2)
                {
                    lastPercent = percent;
                    logProgress($"Loading perf events {percent}%");
                }
            }
        }

        if (endTimeNs < lastTimeNs) endTimeNs = lastTimeNs;
        session.Duration = UTimeSpan.FromMilliseconds((endTimeNs - header.StartMonotonicNs) / 1_000_000.0);

        // A trace recorded without the architecture: the x86_64 kernel text starts at 0xffffffff8..., arm64 at 0xffff8...
        if (header.ProcessArchitecture is null && KernelSymbols is not null)
        {
            var architecture = KernelSymbols.MinAddress >= 0xffffffff80000000 ? Architecture.X64 : Architecture.Arm64;
            foreach (var state in states.Values)
            {
                state.Process.ProcessArchitecture = architecture;
                state.Process.RuntimeIdentifier = architecture == Architecture.X64 ? "linux-x64" : "linux-arm64";
            }
        }

        foreach (var state in states.Values)
        {
            state.Finish(KernelSymbols, buildIds, session.Duration);
        }
    }

    private void ProcessRecord(ReadOnlySpan<byte> span, byte eventIndex, Dictionary<int, ProcessState> states, ref ulong lastTimeNs)
    {
        var type = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var misc = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));

        if (type == PerfEventNative.PERF_RECORD_SAMPLE)
        {
            // ip, pid/tid, time, cpu/res, period, nr, ips[nr]
            var pid = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(16));
            if (!states.TryGetValue(pid, out var state)) return;
            var tid = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20));
            var time = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(24));
            var period = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(40));
            var count = (int)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(48)), (ulong)((span.Length - 56) / 8));
            lastTimeNs = Math.Max(lastTimeNs, time);
            var callChain = MemoryMarshal.Cast<byte, ulong>(span.Slice(56, count * 8));
            if (eventIndex == 0)
            {
                // cpu-clock: the period is the CPU time of the sample in nanoseconds
                state.RecordSample(tid, ToRelativeMs(time), period / 1_000_000.0, callChain);
            }
            else
            {
                // Hardware counter: the period is the number of events the sample stands for
                state.RecordCounterSample(_eventNames.GetValueOrDefault(eventIndex, $"event{eventIndex}"), tid, ToRelativeMs(time), period, callChain);
            }
            return;
        }

        if (type == PerfEventNative.PERF_RECORD_LOST)
        {
            LostRecords += BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16));
            return;
        }

        // The other records end with the sample id: pid, tid, time, cpu, res
        if (span.Length < 8 + 24) return;
        var trailer = span.Slice(span.Length - 24);
        var trailerTime = BinaryPrimitives.ReadUInt64LittleEndian(trailer.Slice(8));
        lastTimeNs = Math.Max(lastTimeNs, trailerTime);

        switch (type)
        {
            case PerfEventNative.PERF_RECORD_MMAP2:
            {
                // pid, tid, addr, len, pgoff, (maj, min, ino, ino_generation | build id), prot, flags, filename
                var pid = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8));
                if (!states.TryGetValue(pid, out var state) || span.Length < 8 + 8 + 24 + 24 + 8) return;
                var addr = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16));
                var len = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(24));
                var pgoff = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(32));
                var prot = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(64));
                if ((prot & 4) == 0) return; // PROT_EXEC
                var path = ReadCString(span.Slice(72, span.Length - 72 - 24));
                var module = state.AddModule(addr, len, pgoff, path, UTimeSpan.FromMilliseconds(ToRelativeMs(trailerTime)));
                if (module is not null && (misc & PerfEventNative.PERF_RECORD_MISC_MMAP_BUILD_ID) != 0)
                {
                    int buildIdSize = span[40];
                    if (buildIdSize is > 0 and <= 20)
                    {
                        module.ModuleFile.SymbolUuid = ToGuid(span.Slice(44, buildIdSize));
                    }
                }
                return;
            }

            case PerfEventNative.PERF_RECORD_COMM:
            {
                var pid = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8));
                if (!states.TryGetValue(pid, out var state)) return;
                var tid = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));
                state.SetName(tid, ReadCString(span.Slice(16, span.Length - 16 - 24)));
                return;
            }

            case PerfEventNative.PERF_RECORD_FORK:
            case PerfEventNative.PERF_RECORD_EXIT:
            {
                // pid, ppid, tid, ptid, time
                var pid = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8));
                if (!states.TryGetValue(pid, out var state) || span.Length < 8 + 24 + 24) return;
                var tid = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16));
                var time = UTimeSpan.FromMilliseconds(ToRelativeMs(BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(24))));
                if (type == PerfEventNative.PERF_RECORD_FORK)
                {
                    state.Thread(tid).StartTime = time;
                }
                else
                {
                    state.Thread(tid).StopTime = time;
                }
                return;
            }
        }
    }

    private double ToRelativeMs(ulong monotonicNs)
    {
        var start = Header.StartMonotonicNs;
        return monotonicNs > start ? (monotonicNs - start) / 1_000_000.0 : 0.0;
    }

    private static string ReadCString(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? bytes.Slice(0, end) : bytes);
    }

    /// <summary>A GUID from the first 16 bytes of a build id (padded with zeros), for the Firefox breakpad id.</summary>
    public static Guid ToGuid(ReadOnlySpan<byte> buildId)
    {
        Span<byte> guid = stackalloc byte[16];
        guid.Clear();
        buildId.Slice(0, Math.Min(16, buildId.Length)).CopyTo(guid);
        return new Guid(guid);
    }

    public void Dispose()
    {
        _reader.Dispose();
    }

    private sealed class ProcessState(UTraceProcess process, double clrTimeOffsetInMs)
    {
        private readonly Dictionary<ulong, UTraceThread> _threads = new();
        private readonly Dictionary<ulong, UTraceNativeModule> _modulesByAddress = new();
        private UnsafeList<UCodeAddressIndex> _callStack = new(256);
        private UAddress _lowestFileBackedModule = ulong.MaxValue;
        private string? _lowestFileBackedModulePath;
        private string? _processName;

        public UTraceProcess Process { get; } = process;

        public UTraceThread Thread(ulong tid)
        {
            if (!_threads.TryGetValue(tid, out var thread))
            {
                thread = Process.Threads.GetOrCreateThread(tid);
                _threads.Add(tid, thread);
            }
            return thread;
        }

        public void SetName(ulong tid, string name)
        {
            if ((int)tid == Process.ProcessID)
            {
                _processName = name;
            }
            Thread(tid).Name = name;
        }

        public UTraceNativeModule? AddModule(ulong start, ulong size, ulong fileOffset, string path, UTimeSpan loadTime)
        {
            if (size == 0 || string.IsNullOrEmpty(path)) return null;
            if (_modulesByAddress.TryGetValue(start, out var existing)) return existing;

            var module = (UTraceNativeModule)Process.Modules.GetOrCreateNativeModule(start, size, path);
            module.FileOffset = fileOffset;
            module.LoadTime = loadTime;
            _modulesByAddress.Add(start, module);

            // The main executable is the file-backed mapping at the lowest address
            if (path[0] == '/' && !path.StartsWith("/memfd:", StringComparison.Ordinal) && start < _lowestFileBackedModule)
            {
                _lowestFileBackedModule = start;
                _lowestFileBackedModulePath = path;
            }
            return module;
        }

        public void RecordSample(ulong tid, double timeInMs, double cpuTimeInMs, ReadOnlySpan<ulong> callChain)
        {
            if (!TryBuildCallStack(timeInMs, callChain, out var callStackIndex)) return;
            var thread = Thread(tid);
            thread.Samples.Add(new UTraceSample(callStackIndex, UTimeSpan.FromMilliseconds(timeInMs), UTimeSpan.FromMilliseconds(cpuTimeInMs)));
            thread.CpuTime = UTimeSpan.FromMilliseconds(thread.CpuTime.InMs + cpuTimeInMs);
        }

        public void RecordCounterSample(string counterName, ulong tid, double timeInMs, ulong count, ReadOnlySpan<ulong> callChain)
        {
            if (!TryBuildCallStack(timeInMs, callChain, out var callStackIndex)) return;
            Thread(tid).GetOrCreateCounterSamples(counterName).Add(new UTraceCounterSample(callStackIndex, UTimeSpan.FromMilliseconds(timeInMs), count));
        }

        private bool TryBuildCallStack(double timeInMs, ReadOnlySpan<ulong> callChain, out UCallStackIndex callStackIndex)
        {
            _callStack.Clear();
            var codeAddresses = Process.CodeAddresses;
            var managedMethods = Process.ManagedMethods;
            var sampleTimeInClrTime = UTimeSpan.FromMilliseconds(timeInMs - clrTimeOffsetInMs);

            // The call chain is leaf first, with context markers between the kernel and user parts
            foreach (var ip in callChain)
            {
                if (ip >= PerfEventNative.PERF_CONTEXT_MAX) continue;
                var address = (UAddress)ip;
                managedMethods.TryFindMethodIndexByAddress(address, sampleTimeInClrTime, out var managedMethodIndex);
                _callStack.Add(codeAddresses.GetOrCreateAddress(address, managedMethodIndex));
            }

            if (_callStack.Count == 0)
            {
                callStackIndex = UCallStackIndex.Invalid;
                return false;
            }

            callStackIndex = Process.CallStacks.InsertCallStack(_callStack.AsSpan());
            return true;
        }

        public void Finish(KernelSymbolResolver? kernelSymbols, Dictionary<string, byte[]> buildIds, UTimeSpan duration)
        {
            Process.FilePath = _lowestFileBackedModulePath ?? _processName ?? string.Empty;

            if (kernelSymbols is not null)
            {
                Process.Modules.GetOrCreateNativeModule(kernelSymbols.MinAddress, kernelSymbols.EndAddress - kernelSymbols.MinAddress, KernelSymbolResolver.ModulePath);
            }

            foreach (var moduleFile in Process.Modules.ModuleFiles)
            {
                if (moduleFile.SymbolUuid == Guid.Empty && buildIds.TryGetValue(moduleFile.FilePath, out var buildId))
                {
                    moduleFile.SymbolUuid = ToGuid(buildId);
                }
            }

            // The ring buffers are read one CPU after the other: a thread that moved between CPUs has its samples out of time order
            foreach (var thread in Process.Threads.Items)
            {
                thread.Samples.SortByTimestamp();
                foreach (var counter in thread.CounterSamples) counter.SortByTimestamp();
            }
        }
    }
}
