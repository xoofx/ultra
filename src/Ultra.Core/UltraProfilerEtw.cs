// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using ByteSizeLib;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;

namespace Ultra.Core;

/// <summary>
/// A profiler that uses Event Tracing for Windows (ETW) to collect performance data.
/// </summary>
internal sealed class UltraProfilerEtw : UltraProfiler
{
    private TraceEventSession? _userSession;
    private TraceEventSession? _kernelSession;

    /// <summary>
    /// The keywords of the CLR provider: the JIT and loader events that resolve the managed frames of the CPU samples, and the
    /// exception, GC (allocation ticks included, so at the verbose level) and interop events that become markers.
    /// </summary>
    /// <remarks>
    /// Without <see cref="ClrTraceEventParser.Keywords.Stack"/>: the runtime would walk the managed stack of every CLR event
    /// (ClrStackWalk), and the converter only uses the stacks of the CPU samples. With an app that writes many CLR events (a WinUI
    /// app reports memory pressure for each WinRT object reference) the profiled app ran up to 80% slower (#32).
    /// </remarks>
    internal const ClrTraceEventParser.Keywords ClrKeywords =
        ClrTraceEventParser.Keywords.JITSymbols |
        ClrTraceEventParser.Keywords.Exception |
        ClrTraceEventParser.Keywords.GC |
        ClrTraceEventParser.Keywords.GCHeapAndTypeNames |
        ClrTraceEventParser.Keywords.Interop |
        ClrTraceEventParser.Keywords.Jit |
        ClrTraceEventParser.Keywords.JittedMethodILToNativeMap |
        ClrTraceEventParser.Keywords.Loader |
        ClrTraceEventParser.Keywords.StartEnumeration;

    /// <summary>
    /// The options of the CLR providers (events and rundown): without an ETW stack per event, which the converter does not use and
    /// which slows down the profiled process for each CLR event (#32). The process filters are added when the profiling starts.
    /// </summary>
    internal static TraceEventProviderOptions CreateClrProviderOptions() => new() { StacksEnabled = false };

    /// <summary>The event id of the CLR ContentionStart event (a thread starts waiting for a lock), the one event of <c>--locks</c> with a stack.</summary>
    internal const int ContentionStartEventId = 81;

    /// <summary>
    /// Initializes a new instance of the <see cref="UltraProfiler"/> class.
    /// </summary>
    internal UltraProfilerEtw()
    {
    }

    private protected override void DisposeImpl()
    {
        DisposeSessions();
    }

    private void DisposeSessions()
    {
        // Dispose stops the OS-level ETW sessions (StopOnDispose is true)
        try { _userSession?.Dispose(); } catch { /* ignore */ }
        _userSession = null;
        try { _kernelSession?.Dispose(); } catch { /* ignore */ }
        _kernelSession = null;
    }

    private protected override ProfilerRunner CreateRunner(UltraProfilerOptions ultraProfilerOptions, List<Process> processList, string baseName, Process? singleProcess)
    {
        var options = CreateClrProviderOptions();

        // Hardware counters (--pmc): parsed before anything starts so that a wrong name fails at once
        var hardwareCounters = ultraProfilerOptions.HardwareCounters.Count > 0
            ? UltraEtwHardwareCounter.ParseAll(ultraProfilerOptions.HardwareCounters, UltraEtwHardwareCounter.GetSources())
            : [];

        var kernelFileName = $"{baseName}.kernel.etl";
        var userFileName = $"{baseName}.user.etl";

        string? etlFinalFile = null;
        string[]? unmergedFiles = null;

        // The runner variable is captured by the closures below to read the up-to-date BaseFileName
        // (Run() appends the pid of the launched process to it after this method returns)
        ProfilerRunner runner = null!;
        runner = new ProfilerRunner(baseName)
        {
            OnStart = () =>
            {
                // Filter the requested process ids
                if (processList.Count > 0)
                {
                    options.ProcessIDFilter = new List<int>();
                    foreach (var process in processList)
                    {
                        options.ProcessIDFilter.Add(process.Id);
                    }
                }

                // Make sure to filter the process name if we have a single process
                if (ultraProfilerOptions.ProgramPath != null)
                {
                    options.ProcessNameFilter = [Path.GetFileName(ultraProfilerOptions.ProgramPath)];
                }

                _userSession = new TraceEventSession($"{baseName}-user", userFileName);
                // TraceEvent enables the PMC profile keyword only on the classic "NT Kernel Logger" session (one per machine:
                // starting it stops the one of another tool), so the kernel session takes that name only with hardware counters
                _kernelSession = new TraceEventSession(hardwareCounters.Count > 0 ? KernelTraceEventParser.KernelSessionName : $"{baseName}-kernel", kernelFileName);

                return Task.CompletedTask;
            },

            OnProfiling = () =>
            {
                var userFileNameLength = new FileInfo(userFileName).Length;
                var kernelFileNameLength = new FileInfo(kernelFileName).Length;
                var totalFileNameLength = userFileNameLength + kernelFileNameLength;
                return totalFileNameLength;
            },

            OnStop = async () =>
            {
                _kernelSession?.Stop();
                _userSession?.Stop();

                await WaitForStaleFile(userFileName, ultraProfilerOptions);
                await WaitForStaleFile(kernelFileName, ultraProfilerOptions);

            },

            OnCatch = () =>
            {
                // Dispose the sessions first (stopping the OS-level ETW sessions) so that the files below
                // are no longer locked and no system-wide session is leaked
                DisposeSessions();

                // Delete intermediate files if we have an exception
                // (ignore errors so that the original exception is not masked)
                try { File.Delete(kernelFileName); } catch { /* ignore */ }
                try { File.Delete(userFileName); } catch { /* ignore */ }

                return Task.CompletedTask;
            },

            OnFinally = () =>
            {
                // Dispose the sessions if they are still alive (e.g. an exception was thrown before OnStop)
                DisposeSessions();

                return Task.CompletedTask;
            },

            FinishFileToConvert = async () =>
            {

                var rundownSession = $"{runner.BaseFileName}.rundown.etl";
                using (TraceEventSession clrRundownSession = new TraceEventSession($"{runner.BaseFileName}-rundown", rundownSession))
                {
                    clrRundownSession.StopOnDispose = true;
                    clrRundownSession.CircularBufferMB = 0;

                    ultraProfilerOptions.LogProgress?.Invoke($"Running CLR Rundown");

                    // The runtime does method rundown first then the module rundown.  This means if you have a large
                    // number of methods and method rundown does not complete you don't get ANYTHING.   To avoid this
                    // we first trigger all module (loader) rundown and then trigger the method rundown
                    clrRundownSession.EnableProvider(
                        ClrRundownTraceEventParser.ProviderGuid,
                        TraceEventLevel.Verbose,
                        (ulong)(ClrRundownTraceEventParser.Keywords.Loader | ClrRundownTraceEventParser.Keywords.ForceEndRundown), options);

                    await Task.Delay(500);

                    clrRundownSession.EnableProvider(
                        ClrRundownTraceEventParser.ProviderGuid,
                        TraceEventLevel.Verbose,
                        (ulong)(ClrRundownTraceEventParser.Keywords.Default & ~ClrRundownTraceEventParser.Keywords.Loader), options);

                    await WaitForStaleFile(rundownSession, ultraProfilerOptions);
                }

                if (StopRequested)
                {
                    throw new InvalidOperationException("CTRL+C requested");
                }

                // The merge rewrites the whole kernel file (~10 GB on a busy machine) to add the image ids (PDB signatures)
                // and volume mappings. Converting on the machine that recorded the trace, TraceEvent reads both from the
                // local files, so the fast filter reads the unmerged files directly unless the merged ETL file is kept.
                if (ultraProfilerOptions.FastFilter && !ultraProfilerOptions.KeepMergedEtl)
                {
                    unmergedFiles = [kernelFileName, userFileName, rundownSession];
                    return unmergedFiles.Where(File.Exists).Select(x => new UltraProfilerTraceFile(x)).ToList();
                }

                ultraProfilerOptions.LogProgress?.Invoke($"Merging ETL Files");
                // Merge file (and to force Volume mapping)
                etlFinalFile = $"{ultraProfilerOptions.BaseOutputFileName ?? runner.BaseFileName}.etl";
                TraceEventSession.Merge([kernelFileName, userFileName, rundownSession], etlFinalFile);
                //TraceEventSession.Merge([kernelFileName, userFileName], $"{baseName}.etl");

                if (StopRequested)
                {
                    throw new InvalidOperationException("CTRL+C requested");
                }

                if (!ultraProfilerOptions.KeepEtlIntermediateFiles)
                {
                    File.Delete(kernelFileName);
                    File.Delete(userFileName);
                    File.Delete(rundownSession);
                }

                return [new(etlFinalFile)];
            },

            OnFinalCleanup = () =>
            {
                if (unmergedFiles is not null)
                {
                    File.Delete(UltraEtwFastFilter.GetFilteredFileName(unmergedFiles[0], processList.Select(x => x.Id)));
                    if (!ultraProfilerOptions.KeepEtlIntermediateFiles)
                    {
                        foreach (var file in unmergedFiles)
                        {
                            File.Delete(file);
                        }
                    }
                }

                if (!ultraProfilerOptions.KeepMergedEtl && etlFinalFile is not null)
                {
                    File.Delete(etlFinalFile);
                    var etlxFinalFile = Path.ChangeExtension(etlFinalFile, ".etlx");
                    File.Delete(etlxFinalFile);

                    if (ultraProfilerOptions.FastFilter)
                    {
                        File.Delete(UltraEtwFastFilter.GetFilteredFileName(etlFinalFile, processList.Select(x => x.Id)));
                    }
                }

                return Task.CompletedTask;
            },

            OnEnablingProfiling = () =>
            {
                _kernelSession!.StopOnDispose = true;
                _kernelSession.CircularBufferMB = 0;
                _kernelSession.CpuSampleIntervalMSec = ultraProfilerOptions.CpuSamplingIntervalInMs;
                _kernelSession.StackCompression = false;

                _userSession!.StopOnDispose = true;
                _userSession.CircularBufferMB = 0;
                _userSession.CpuSampleIntervalMSec = ultraProfilerOptions.CpuSamplingIntervalInMs;
                _userSession.StackCompression = false;

                var kernelEvents = KernelTraceEventParser.Keywords.Profile
                                   | KernelTraceEventParser.Keywords.ContextSwitch
                                   | KernelTraceEventParser.Keywords.ImageLoad
                                   | KernelTraceEventParser.Keywords.Process
                                   | KernelTraceEventParser.Keywords.Thread;
                var kernelStacks = KernelTraceEventParser.Keywords.Profile;

                if (hardwareCounters.Count > 0)
                {
                    // Another tool (WPR, PerfView, xperf) may hold the NT Kernel Logger. TraceEvent would restart it in place, which
                    // keeps garbled kernel flags (no CPU samples, context switches or thread rundown): stop it first so ours starts afresh
                    using (var runningKernelSession = TraceEventSession.GetActiveSession(KernelTraceEventParser.KernelSessionName))
                    {
                        if (runningKernelSession is not null)
                        {
                            (ultraProfilerOptions.LogWarning ?? ultraProfilerOptions.LogProgress)?.Invoke($"Warning: the {KernelTraceEventParser.KernelSessionName} session is already running (WPR, PerfView, xperf...): it is stopped to sample the hardware counters");
                            runningKernelSession.Stop();
                        }
                    }

                    // The selection of the sources is system wide and must be set before the kernel session starts.
                    // The kernel logs a PerfInfo CollectionStart event per source with its interval, which the converter reads.
                    try
                    {
                        TraceEventProfileSources.Set(hardwareCounters.Select(x => x.SourceId).ToArray(), hardwareCounters.Select(x => x.Interval).ToArray());
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Cannot select the hardware counters {string.Join(", ", hardwareCounters.Select(x => x.Name))} (too many at once, or the counters are in use by another tool): {ex.Message}", ex);
                    }

                    kernelEvents |= KernelTraceEventParser.Keywords.PMCProfile;
                    kernelStacks |= KernelTraceEventParser.Keywords.PMCProfile;
                    (ultraProfilerOptions.LogWarning ?? ultraProfilerOptions.LogProgress)?.Invoke($"Hardware counters: {string.Join(", ", hardwareCounters.Select(x => $"{x.Name} every {x.Interval:N0} events"))}");
                }

                _kernelSession.EnableKernelProvider(kernelEvents, kernelStacks);

                // --locks: the contention events, with the stack of the ContentionStart event only (where the thread waits): they are few
                // compared to the other CLR events, a stack on all of them slowed the profiled app down (#32)
                var clrKeywords = ClrKeywords;
                var clrOptions = options;
                if (ultraProfilerOptions.LockContention)
                {
                    clrKeywords |= ClrTraceEventParser.Keywords.Contention;
                    clrOptions = CreateClrProviderOptions();
                    clrOptions.ProcessIDFilter = options.ProcessIDFilter;
                    clrOptions.ProcessNameFilter = options.ProcessNameFilter;
                    clrOptions.EventIDStacksToEnable = [ContentionStartEventId];
                }

                _userSession.EnableProvider(
                    ClrTraceEventParser.ProviderGuid,
                    TraceEventLevel.Verbose, // The GC allocation ticks are verbose events
                    (ulong)clrKeywords, clrOptions);


                // Reset the clock to account for the duration of the profiler
                ProfilerClock.Restart();

                return Task.CompletedTask;
            }
        };

        return runner;
    }
}