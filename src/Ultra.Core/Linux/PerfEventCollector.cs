// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;

namespace Ultra.Core.Linux;

/// <summary>
/// Records system-wide perf events (CPU samples with call chains, mmap/comm/fork/exit records and context switches
/// of all CPUs, plus the samples of optional hardware counters) into a .ultraperf file. Samples of processes other
/// than the profiled ones, and context switches between two other processes, are dropped while recording; the other
/// records are kept for all processes.
/// </summary>
internal sealed unsafe class PerfEventCollector : IDisposable
{
    /// <summary>Ring buffer data pages of a hardware counter per CPU (256 x 4 KB = 1 MB): their rate is capped by the kernel.</summary>
    private const int CounterDataPages = 256;

    private readonly UltraPerfFileWriter _writer;
    private readonly PerfEventAttr[] _attrs;
    private readonly string[] _eventNames;
    private readonly int _dataPages;
    private readonly List<(PerfEventBuffer Buffer, byte EventIndex)> _buffers = new();
    private readonly byte[] _scratch = new byte[PerfRingReader.MaxRecordSize + 8];
    private PerfRecordHandler[] _handlers = [];
    private volatile int[] _processIds;
    private volatile bool _acceptAllProcesses;
    private Thread? _pumpThread;
    private volatile bool _stopRequested;
    private long _records, _lostRecords, _droppedSamples, _droppedSwitches;
    private Exception? _pumpException;

    /// <summary>
    /// Creates the collector.
    /// </summary>
    /// <param name="writer">The file to write to.</param>
    /// <param name="samplingPeriodNs">The CPU sampling period in nanoseconds.</param>
    /// <param name="dataPages">The ring buffer data pages per CPU of the CPU samples (a power of two; 1024 = 4 MB).</param>
    /// <param name="processIds">The processes whose samples are kept.</param>
    /// <param name="acceptAllProcessesUntilKnown">True when a process is about to be launched: samples of all processes are kept until <see cref="AddProcess"/> is called.</param>
    /// <param name="counters">The hardware counters to sample in addition to the CPU time (event index 1...).</param>
    public PerfEventCollector(UltraPerfFileWriter writer, ulong samplingPeriodNs, int dataPages, IEnumerable<int> processIds, bool acceptAllProcessesUntilKnown, IReadOnlyList<PerfEventSpec>? counters = null)
    {
        _writer = writer;
        _dataPages = dataPages;
        _processIds = processIds.ToArray();
        _acceptAllProcesses = acceptAllProcessesUntilKnown;

        counters ??= [];
        _attrs = new PerfEventAttr[1 + counters.Count];
        _eventNames = new string[1 + counters.Count];
        _attrs[0] = CreateCpuClockAttr(samplingPeriodNs);
        _eventNames[0] = CpuClockEventName;
        for (int i = 0; i < counters.Count; i++)
        {
            _attrs[1 + i] = CreateCounterAttr(counters[i]);
            _eventNames[1 + i] = counters[i].Name;
        }
    }

    /// <summary>The name of event 0.</summary>
    public const string CpuClockEventName = "cpu-clock";

    /// <summary>The sample_type of the recorded samples (fixed layout: ip, pid/tid, time, cpu, period, call chain).</summary>
    public const ulong SampleType = PerfEventNative.PERF_SAMPLE_IP | PerfEventNative.PERF_SAMPLE_TID | PerfEventNative.PERF_SAMPLE_TIME | PerfEventNative.PERF_SAMPLE_CALLCHAIN | PerfEventNative.PERF_SAMPLE_CPU | PerfEventNative.PERF_SAMPLE_PERIOD;

    /// <summary>The offset of the pid in a sample record (header, ip, then pid).</summary>
    private const int SamplePidOffset = 8 + 8;

    /// <summary>The attributes of the recorded events: 0 = cpu-clock, then the counters.</summary>
    public IReadOnlyList<PerfEventAttr> Attrs => _attrs;

    /// <summary>The names of the recorded events, same order as <see cref="Attrs"/>.</summary>
    public IReadOnlyList<string> EventNames => _eventNames;

    public long Records => Volatile.Read(ref _records);

    public long LostRecords => Volatile.Read(ref _lostRecords);

    public long DroppedSamples => Volatile.Read(ref _droppedSamples);

    /// <summary>The context switches between other processes, not written.</summary>
    public long DroppedSwitches => Volatile.Read(ref _droppedSwitches);

    private const ulong CommonFlags = PerfEventNative.ATTR_DISABLED | PerfEventNative.ATTR_EXCLUDE_IDLE | PerfEventNative.ATTR_WATERMARK
                                      | PerfEventNative.ATTR_SAMPLE_ID_ALL | PerfEventNative.ATTR_USE_CLOCKID;

    /// <summary>
    /// The attributes of a system-wide cpu-clock sampling event with call chains and context switches, on the monotonic clock.
    /// Only this event carries the mmap/comm/fork/exit/switch records.
    /// </summary>
    public static PerfEventAttr CreateCpuClockAttr(ulong samplingPeriodNs)
    {
        return new PerfEventAttr
        {
            type = PerfEventNative.PERF_TYPE_SOFTWARE,
            size = PerfEventAttr.Size,
            config = PerfEventNative.PERF_COUNT_SW_CPU_CLOCK,
            sample_period_or_freq = samplingPeriodNs,
            sample_type = SampleType,
            flags = CommonFlags | PerfEventNative.ATTR_MMAP | PerfEventNative.ATTR_COMM | PerfEventNative.ATTR_TASK
                    | PerfEventNative.ATTR_MMAP2 | PerfEventNative.ATTR_CONTEXT_SWITCH | PerfEventNative.ATTR_BUILD_ID,
            clockid = PerfEventNative.CLOCK_MONOTONIC,
            wakeup_events_or_watermark = 64 * 1024, // wake the reader every 64 KB
            sample_max_stack = 127,
        };
    }

    /// <summary>
    /// The attributes of a system-wide hardware counter sampling event with call chains (same sample layout as the CPU samples).
    /// </summary>
    public static PerfEventAttr CreateCounterAttr(PerfEventSpec counter)
    {
        return new PerfEventAttr
        {
            type = counter.Type,
            size = PerfEventAttr.Size,
            config = counter.Config,
            sample_period_or_freq = counter.SamplePeriodOrFrequency,
            sample_type = SampleType,
            flags = CommonFlags | (counter.IsFrequency ? PerfEventNative.ATTR_FREQ : 0),
            clockid = PerfEventNative.CLOCK_MONOTONIC,
            wakeup_events_or_watermark = 64 * 1024,
            sample_max_stack = 127,
        };
    }

    /// <summary>
    /// Opens the events of all CPUs (disabled).
    /// </summary>
    /// <exception cref="PerfEventException">Thrown when the CPU sampling event cannot be opened (no permission, unsupported kernel).</exception>
    /// <exception cref="InvalidOperationException">Thrown when a hardware counter is not available on this machine.</exception>
    public void Open()
    {
        var cpuClockAttr = _attrs[0];
        foreach (var cpu in OnlineCpuIds())
        {
            try
            {
                _buffers.Add((PerfEventBuffer.Open(ref cpuClockAttr, cpu, _dataPages), 0));
            }
            catch (PerfEventException ex) when (ex.Errno == PerfEventNative.EINVAL && cpu == 0 && (cpuClockAttr.flags & PerfEventNative.ATTR_BUILD_ID) != 0)
            {
                // Kernel older than 5.12: no build ids in the mmap records (the snapshot has them for the initial mappings)
                cpuClockAttr.flags &= ~PerfEventNative.ATTR_BUILD_ID;
                _buffers.Add((PerfEventBuffer.Open(ref cpuClockAttr, cpu, _dataPages), 0));
            }

            for (int i = 1; i < _attrs.Length; i++)
            {
                var attr = _attrs[i];
                try
                {
                    _buffers.Add((PerfEventBuffer.Open(ref attr, cpu, CounterDataPages), (byte)i));
                }
                catch (PerfEventException ex)
                {
                    throw new InvalidOperationException($"The hardware counter `{_eventNames[i]}` is not available on this machine or in this virtual machine (errno {ex.Errno})", ex);
                }
            }
        }

        _handlers = new PerfRecordHandler[_buffers.Count];
        for (int i = 0; i < _buffers.Count; i++)
        {
            ushort cpu = (ushort)_buffers[i].Buffer.Cpu;
            byte eventIndex = _buffers[i].EventIndex;
            _handlers[i] = record => OnRecord(cpu, eventIndex, record);
        }
    }

    /// <summary>
    /// Keeps the samples of this process from now on (and stops keeping the samples of all processes).
    /// </summary>
    public void AddProcess(int pid)
    {
        var pids = _processIds;
        if (Array.IndexOf(pids, pid) < 0)
        {
            var newPids = new int[pids.Length + 1];
            pids.CopyTo(newPids, 0);
            newPids[pids.Length] = pid;
            _processIds = newPids;
        }
        _acceptAllProcesses = false;
    }

    /// <summary>
    /// Enables the events and starts the reader thread.
    /// </summary>
    public void Start()
    {
        foreach (var (buffer, _) in _buffers) buffer.Enable();
        _pumpThread = new Thread(Pump) { Name = "Ultra-PerfEventPump", IsBackground = true };
        _pumpThread.Start();
    }

    /// <summary>
    /// Disables the events, drains the ring buffers and writes the end frame.
    /// </summary>
    public void Stop()
    {
        foreach (var (buffer, _) in _buffers) buffer.Disable();
        _stopRequested = true;
        _pumpThread?.Join();
        _pumpThread = null;
        if (_pumpException is not null) throw new InvalidOperationException("The perf event reader thread failed", _pumpException);

        DrainAll();
        _writer.WriteFrame(UltraPerfFrameKind.End, 0, 0, UltraPerfPayload.EncodeEnd(new UltraPerfPayload.End((ulong)_lostRecords, (ulong)_records, (ulong)_droppedSamples, PerfEventNative.MonotonicNanoseconds())));
    }

    private void Pump()
    {
        try
        {
            var pollFds = new PollFd[_buffers.Count];
            for (int i = 0; i < pollFds.Length; i++) pollFds[i] = new PollFd { fd = _buffers[i].Buffer.Fd, events = PerfEventNative.POLLIN };

            while (!_stopRequested)
            {
                fixed (PollFd* p = pollFds)
                {
                    PerfEventNative.poll(p, (nuint)pollFds.Length, 100);
                }
                DrainAll();
            }
        }
        catch (Exception ex)
        {
            _pumpException = ex;
        }
    }

    private void DrainAll()
    {
        for (int i = 0; i < _buffers.Count; i++)
        {
            _buffers[i].Buffer.Drain(_scratch, _handlers[i]);
        }
    }

    private void OnRecord(ushort cpu, byte eventIndex, ReadOnlySpan<byte> record)
    {
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(record);
        if (type == PerfEventNative.PERF_RECORD_SAMPLE)
        {
            if (!_acceptAllProcesses)
            {
                int pid = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(SamplePidOffset));
                if (Array.IndexOf(_processIds, pid) < 0)
                {
                    _droppedSamples++;
                    return;
                }
            }
        }
        else if (type == PerfEventNative.PERF_RECORD_SWITCH_CPU_WIDE)
        {
            // The context switches of the whole machine are the bulk of a long recording (tens of thousands a second on a big box):
            // keep those with a profiled process on either side (the switching thread is in the sample id at the end, the other in the body)
            if (!_acceptAllProcesses && record.Length >= 8 + 8 + 24)
            {
                int otherPid = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8));
                int pid = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(record.Length - 24));
                if (Array.IndexOf(_processIds, pid) < 0 && Array.IndexOf(_processIds, otherPid) < 0)
                {
                    _droppedSwitches++;
                    return;
                }
            }
        }
        else if (type == PerfEventNative.PERF_RECORD_LOST)
        {
            _lostRecords += (long)BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(16));
        }

        _records++;
        _writer.WriteRecord(cpu, eventIndex, record);
    }

    public void Dispose()
    {
        _stopRequested = true;
        _pumpThread?.Join();
        _pumpThread = null;
        foreach (var (buffer, _) in _buffers) buffer.Dispose();
        _buffers.Clear();
    }

    /// <summary>
    /// The ids of the online CPUs, from /sys/devices/system/cpu/online: the machine's CPUs, not the ones this process may run on
    /// (Environment.ProcessorCount follows the affinity mask and the cgroup quota, and CPU ids are not 0..count-1 then). Falls back to
    /// 0..ProcessorCount-1 when the file cannot be read.
    /// </summary>
    public static IReadOnlyList<int> OnlineCpuIds()
    {
        try
        {
            var ids = ParseCpuList(File.ReadAllText("/sys/devices/system/cpu/online"));
            if (ids.Count > 0) return ids;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
        }
        return Enumerable.Range(0, Environment.ProcessorCount).ToList();
    }

    /// <summary>
    /// Parses a kernel CPU list: "0-31", "0-3,8-11", "0,2,4".
    /// </summary>
    /// <exception cref="FormatException">Not a CPU list.</exception>
    public static IReadOnlyList<int> ParseCpuList(string text)
    {
        var ids = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                ids.Add(int.Parse(part, System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }
            int first = int.Parse(part.AsSpan(0, dash), System.Globalization.CultureInfo.InvariantCulture);
            int last = int.Parse(part.AsSpan(dash + 1), System.Globalization.CultureInfo.InvariantCulture);
            if (last < first || last - first > 4096) throw new FormatException($"Invalid CPU range `{part}`");
            for (int cpu = first; cpu <= last; cpu++) ids.Add(cpu);
        }
        return ids;
    }

    /// <summary>
    /// True when the current user can open system-wide perf events: root, or kernel.perf_event_paranoid &lt;= 0.
    /// </summary>
    public static bool HasPerfAccess()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            if (PerfEventNative.geteuid() == 0) return true;
            var text = File.ReadAllText("/proc/sys/kernel/perf_event_paranoid").Trim();
            return int.TryParse(text, out var paranoid) && paranoid <= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
