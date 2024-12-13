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
    /// Initializes a new instance of the <see cref="UltraProfiler"/> class.
    /// </summary>
    internal UltraProfilerEtw()
    {
    }

    private protected override void DisposeImpl()
    {
        _userSession?.Dispose();
        _userSession = null;
        _kernelSession?.Dispose();
        _kernelSession = null;
    }

    private protected override ProfilerRunner CreateRunner(UltraProfilerOptions ultraProfilerOptions, List<Process> processList, string baseName, Process? singleProcess)
    {
        var options = new TraceEventProviderOptions()
        {
            StacksEnabled = true,
        };

        var kernelFileName = $"{baseName}.kernel.etl";
        var userFileName = $"{baseName}.user.etl";

        string? etlFinalFile = null;

        var runner = new ProfilerRunner(baseName)
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
                _kernelSession = new TraceEventSession($"{baseName}-kernel", kernelFileName);

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
                // Delete intermediate files if we have an exception
                File.Delete(kernelFileName);
                File.Delete(userFileName);

                return Task.CompletedTask;
            },

            OnFinally = () =>
            {
                _userSession = null;
                _kernelSession = null;

                return Task.CompletedTask;
            },

            FinishFileToConvert = async () =>
            {

                var rundownSession = $"{baseName}.rundown.etl";
                using (TraceEventSession clrRundownSession = new TraceEventSession($"{baseName}-rundown", rundownSession))
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

                ultraProfilerOptions.LogProgress?.Invoke($"Merging ETL Files");
                // Merge file (and to force Volume mapping)
                etlFinalFile = $"{ultraProfilerOptions.BaseOutputFileName ?? baseName}.etl";
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
                if (!ultraProfilerOptions.KeepMergedEtl && etlFinalFile is not null)
                {
                    File.Delete(etlFinalFile);
                    var etlxFinalFile = Path.ChangeExtension(etlFinalFile, ".etlx");
                    File.Delete(etlxFinalFile);
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
                _kernelSession.EnableKernelProvider(kernelEvents, KernelTraceEventParser.Keywords.Profile);

                var jitEvents = ClrTraceEventParser.Keywords.JITSymbols |
                                ClrTraceEventParser.Keywords.Exception |
                                ClrTraceEventParser.Keywords.GC |
                                ClrTraceEventParser.Keywords.GCHeapAndTypeNames |
                                ClrTraceEventParser.Keywords.Interop |
                                ClrTraceEventParser.Keywords.JITSymbols |
                                ClrTraceEventParser.Keywords.Jit |
                                ClrTraceEventParser.Keywords.JittedMethodILToNativeMap |
                                ClrTraceEventParser.Keywords.Loader |
                                ClrTraceEventParser.Keywords.Stack |
                                ClrTraceEventParser.Keywords.StartEnumeration;

                _userSession.EnableProvider(
                    ClrTraceEventParser.ProviderGuid,
                    TraceEventLevel.Verbose, // For call stacks.
                    (ulong)jitEvents, options);


                // Reset the clock to account for the duration of the profiler
                ProfilerClock.Restart();

                return Task.CompletedTask;
            }
        };

        return runner;
    }
}