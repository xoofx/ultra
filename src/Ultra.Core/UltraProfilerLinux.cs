// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Text;
using Ultra.Core.Linux;

namespace Ultra.Core;

/// <summary>
/// A profiler that uses the Linux kernel's perf events (perf_event_open) for the CPU samples, call chains and
/// context switches, and an EventPipe session for the .NET runtime events (GC, JIT, method addresses).
/// </summary>
internal sealed class UltraProfilerLinux : UltraProfiler
{
    /// <summary>Ring buffer data pages per CPU: 1024 x 4 KB = 4 MB, enough for 8 kHz on a busy core between two reads.</summary>
    private const int DataPagesPerCpu = 1024;

    private UltraPerfFileWriter? _writer;
    private PerfEventCollector? _collector;
    private readonly List<DiagnosticPortSession> _clrSessions = new();
    private readonly HashSet<int> _processesStarted = new();
    private bool _profilingEnabled;

    internal UltraProfilerLinux()
    {
    }

    private protected override void DisposeImpl()
    {
        _collector?.Dispose();
        _collector = null;
        _writer?.Dispose();
        _writer = null;
    }

    private protected override ProfilerRunner CreateRunner(UltraProfilerOptions ultraProfilerOptions, List<Process> processList, string baseName, Process? singleProcess)
    {
        var perfFileName = $"{baseName}.ultraperf";
        var samplingPeriodNs = (ulong)Math.Max(1_000.0, ultraProfilerOptions.CpuSamplingIntervalInMs * 1_000_000.0);
        var counters = ultraProfilerOptions.HardwareCounters.Select(x => PerfEventSpec.Parse(x, (ulong)Math.Max(1, ultraProfilerOptions.HardwareCounterFrequency))).ToList();

        var runner = new ProfilerRunner(baseName)
        {
            OnStart = () =>
            {
                if (!PerfEventCollector.HasPerfAccess())
                {
                    throw new InvalidOperationException("Profiling on Linux requires root (sudo) or kernel.perf_event_paranoid <= 0");
                }

                var header = new UltraPerfHeader
                {
                    CpuCount = PerfEventCollector.OnlineCpuIds().Count,
                    SamplingPeriodNs = samplingPeriodNs,
                    StartMonotonicNs = PerfEventNative.MonotonicNanoseconds(),
                    StartUtcTicks = DateTime.UtcNow.Ticks,
                    SampleType = PerfEventCollector.SampleType,
                    ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,
                };
                header.ProcessIds.AddRange(processList.Select(x => x.Id));
                header.Events.Add(PerfEventCollector.CreateCpuClockAttr(samplingPeriodNs));
                header.Events.AddRange(counters.Select(PerfEventCollector.CreateCounterAttr));

                _writer = new UltraPerfFileWriter(perfFileName, header);
                _collector = new PerfEventCollector(_writer, samplingPeriodNs, DataPagesPerCpu, processList.Select(x => x.Id), ultraProfilerOptions.ProgramPath is not null, counters);
                for (int i = 0; i < _collector.EventNames.Count; i++)
                {
                    _writer.WriteFrame(UltraPerfFrameKind.EventName, 0, 0, UltraPerfPayload.EncodeEventName((byte)i, _collector.EventNames[i]));
                }

                // Open the events now so that a permission or kernel problem fails before anything is started
                _collector.Open();

                return Task.CompletedTask;
            },

            OnEnablingProfiling = async () =>
            {
                var kallsyms = ProcSnapshot.ReadKallsyms();
                if (kallsyms.Length > 0)
                {
                    _writer!.WriteFrame(UltraPerfFrameKind.Kallsyms, 0, 0, Encoding.UTF8.GetBytes(kallsyms));
                }

                // The processes attached to, and a program launched before this with --delay or --paused
                foreach (var process in processList)
                {
                    await StartProfilingProcess(process.Id, baseName, ultraProfilerOptions);
                }

                _profilingEnabled = true;
                _collector!.Start();
            },

            // Called for a launched program: after OnEnablingProfiling normally, before it with --delay or --paused (then
            // OnEnablingProfiling takes the process from the list, so nothing is recorded during the pause and once only)
            OnProcessStarted = async (process) =>
            {
                _collector!.AddProcess(process.Id);
                if (_profilingEnabled)
                {
                    await StartProfilingProcess(process.Id, baseName, ultraProfilerOptions);
                }
            },

            OnProfiling = () => (_writer?.Length ?? 0) + _clrSessions.Sum(x => x.GetNettraceFileLength()),

            OnStop = async () =>
            {
                _collector?.Stop();

                // Stopping the CLR session triggers the method rundown in the profiled process
                foreach (var session in _clrSessions)
                {
                    await session.StopAndDisposeAsync();
                }

                _writer?.Dispose();
                _writer = null;
                ultraProfilerOptions.LogProgress?.Invoke($"Recorded {_collector?.Records ?? 0} perf records, lost {_collector?.LostRecords ?? 0}, dropped {_collector?.DroppedSamples ?? 0} samples and {_collector?.DroppedSwitches ?? 0} context switches of other processes");
            },

            OnCatch = async () =>
            {
                DisposeImpl();
                foreach (var session in _clrSessions)
                {
                    try { await session.StopAndDisposeAsync(); } catch { /* ignore */ }
                }
                try { File.Delete(perfFileName); } catch { /* ignore */ }
                foreach (var session in _clrSessions)
                {
                    if (session.TryGetNettraceFilePathIfExists(out var path))
                    {
                        try { File.Delete(path); } catch { /* ignore */ }
                    }
                }
            },

            OnFinally = () =>
            {
                DisposeImpl();
                return Task.CompletedTask;
            },

            FinishFileToConvert = () =>
            {
                var files = new List<UltraProfilerTraceFile> { new(perfFileName) };
                foreach (var session in _clrSessions)
                {
                    if (session.TryGetNettraceFilePathIfExists(out var path))
                    {
                        files.Add(new UltraProfilerTraceFile(path));
                    }
                }
                return Task.FromResult(files);
            },

            OnFinalCleanup = () =>
            {
                if (!ultraProfilerOptions.KeepEtlIntermediateFiles && !ultraProfilerOptions.KeepMergedEtl)
                {
                    File.Delete(perfFileName);
                    foreach (var session in _clrSessions)
                    {
                        if (session.TryGetNettraceFilePathIfExists(out var path))
                        {
                            File.Delete(path);
                        }
                    }
                }
                return Task.CompletedTask;
            },
        };

        return runner;
    }

    private void WriteProcessSnapshot(int pid)
    {
        var writer = _writer!;
        var buildIdDone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var map in ProcSnapshot.ReadExecutableMaps(pid))
        {
            writer.WriteFrame(UltraPerfFrameKind.MapSnapshot, 0, 0, UltraPerfPayload.EncodeMap(map));
            if (map.Path.Length > 0 && map.Path[0] == '/' && buildIdDone.Add(map.Path))
            {
                var buildId = ProcSnapshot.TryReadElfBuildId(map.Path);
                if (buildId is not null)
                {
                    writer.WriteFrame(UltraPerfFrameKind.BuildId, 0, 0, UltraPerfPayload.EncodeBuildId(new UltraPerfPayload.BuildId(map.Path, buildId)));
                }
            }
        }

        foreach (var comm in ProcSnapshot.ReadComms(pid))
        {
            writer.WriteFrame(UltraPerfFrameKind.CommSnapshot, 0, 0, UltraPerfPayload.EncodeComm(comm));
        }
    }

    /// <summary>
    /// What the event stream will not tell us about a process that already runs (its mappings, thread names), then its CLR session;
    /// once per process.
    /// </summary>
    private async Task StartProfilingProcess(int pid, string baseName, UltraProfilerOptions options)
    {
        if (!_processesStarted.Add(pid)) return;
        WriteProcessSnapshot(pid);

        // A non .NET process has no diagnostic port: the session then never connects and is cancelled at stop
        var session = new DiagnosticPortSession(pid, false, baseName, CancellationToken);
        _clrSessions.Add(session);
        await session.StartProfiling(CancellationToken);
    }
}
