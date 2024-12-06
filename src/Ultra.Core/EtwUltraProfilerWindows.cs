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
public sealed class EtwUltraProfilerWindows : EtwUltraProfiler
{
    private TraceEventSession? _userSession;
    private TraceEventSession? _kernelSession;

    /// <summary>
    /// Initializes a new instance of the <see cref="EtwUltraProfiler"/> class.
    /// </summary>
    internal EtwUltraProfilerWindows()
    {
    }

    private protected override void DisposeImpl()
    {
        _userSession?.Dispose();
        _userSession = null;
        _kernelSession?.Dispose();
        _kernelSession = null;
    }

    private protected override async Task<string> RunImpl(EtwUltraProfilerOptions ultraProfilerOptions, List<Process> processList, string baseName, Process? singleProcess)
    {
        var options = new TraceEventProviderOptions()
        {
            StacksEnabled = true,
        };

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

        var kernelFileName = $"{baseName}.kernel.etl";
        var userFileName = $"{baseName}.user.etl";

        _profilerClock.Restart();
        _lastTimeProgress = _profilerClock.Elapsed;

        _userSession = new TraceEventSession($"{baseName}-user", userFileName);
        _kernelSession = new TraceEventSession($"{baseName}-kernel", kernelFileName);

        try
        {
            using (_userSession)
            using (_kernelSession)
            {
                var startTheRequestedProgramIfRequired = () =>
                {
                    // Start a command line process if needed
                    if (ultraProfilerOptions.ProgramPath is not null)
                    {
                        var processState = StartProcess(ultraProfilerOptions);
                        processList.Add(processState.Process);
                        // Append the pid for a single process that we are attaching to
                        if (singleProcess is null)
                        {
                            baseName = $"{baseName}_pid_{processState.Process.Id}";
                        }

                        singleProcess ??= processState.Process;
                    }
                };

                // If we have a delay, or we are asked to start paused, we start the process before the profiling starts
                bool hasExplicitProgramHasStarted = ultraProfilerOptions.DelayInSeconds != 0.0 || ultraProfilerOptions.Paused;
                if (hasExplicitProgramHasStarted)
                {
                    startTheRequestedProgramIfRequired();
                }

                // Wait for the process to start
                if (ultraProfilerOptions.Paused)
                {
                    while (!ultraProfilerOptions.ShouldStartProfiling!() && !_cancelRequested && !_stopRequested)
                    {
                    }

                    // If we have a cancel request, we don't start the profiling
                    if (_cancelRequested || _stopRequested)
                    {
                        throw new InvalidOperationException("CTRL+C requested");
                    }
                }

                await EnableProfiling(options, ultraProfilerOptions);

                // If we haven't started the program yet, we start it now (for explicit program path)
                if (!hasExplicitProgramHasStarted)
                {
                    startTheRequestedProgramIfRequired();
                }

                foreach (var process in processList)
                {
                    ultraProfilerOptions.LogProgress?.Invoke($"Start Profiling Process {process.ProcessName} ({process.Id})");
                }

                // Collect the data until all processes have exited or there is a cancel request
                HashSet<Process> exitedProcessList = new();
                while (!_cancelRequested)
                {
                    // Exit if we have reached the duration
                    if (_profilerClock.Elapsed.TotalSeconds > ultraProfilerOptions.DurationInSeconds)
                    {
                        ultraProfilerOptions.LogProgress?.Invoke($"Stopping profiling, max duration reached at {ultraProfilerOptions.DurationInSeconds}s");
                        break;
                    }

                    if (_profilerClock.Elapsed.TotalMilliseconds - _lastTimeProgress.TotalMilliseconds > ultraProfilerOptions.UpdateLogAfterInMs)
                    {
                        var userFileNameLength = new FileInfo(userFileName).Length;
                        var kernelFileNameLength = new FileInfo(kernelFileName).Length;
                        var totalFileNameLength = userFileNameLength + kernelFileNameLength;

                        ultraProfilerOptions.LogStepProgress?.Invoke(singleProcess is not null
                            ? $"Profiling Process {singleProcess.ProcessName} ({singleProcess.Id}) - {(int)_profilerClock.Elapsed.TotalSeconds}s - {ByteSize.FromBytes(totalFileNameLength)}"
                            : $"Profiling {processList.Count} Processes - {(int)_profilerClock.Elapsed.TotalSeconds}s - {ByteSize.FromBytes(totalFileNameLength)}");
                        _lastTimeProgress = _profilerClock.Elapsed;
                    }

                    await Task.Delay(ultraProfilerOptions.CheckDeltaTimeInMs);

                    foreach (var process in processList)
                    {
                        if (process.HasExited && exitedProcessList.Add(process))
                        {
                            ultraProfilerOptions.LogProgress?.Invoke($"Process {process.ProcessName} ({process.Id}) has exited");
                        }
                    }

                    if (exitedProcessList.Count == processList.Count)
                    {
                        break;
                    }

                } // Needed for JIT Compile code that was already compiled.

                _kernelSession.Stop();
                _userSession.Stop();

                ultraProfilerOptions.LogProgress?.Invoke(singleProcess is not null ? $"End Profiling Process" : $"End Profiling {processList.Count} Processes");

                await WaitForStaleFile(userFileName, ultraProfilerOptions);
                await WaitForStaleFile(kernelFileName, ultraProfilerOptions);
            }
        }
        catch
        {
            // Delete intermediate files if we have an exception
            File.Delete(kernelFileName);
            File.Delete(userFileName);
            throw;
        }
        finally
        {
            _userSession = null;
            _kernelSession = null;
            _cleanCancel?.Set();
        }

        if (_stopRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

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

        if (_stopRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        ultraProfilerOptions.LogProgress?.Invoke($"Merging ETL Files");
        // Merge file (and to force Volume mapping)
        var etlFinalFile = $"{ultraProfilerOptions.BaseOutputFileName ?? baseName}.etl";
        TraceEventSession.Merge([kernelFileName, userFileName, rundownSession], etlFinalFile);
        //TraceEventSession.Merge([kernelFileName, userFileName], $"{baseName}.etl");

        if (_stopRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        if (!ultraProfilerOptions.KeepEtlIntermediateFiles)
        {
            File.Delete(kernelFileName);
            File.Delete(userFileName);
            File.Delete(rundownSession);
        }

        if (_stopRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        var jsonFinalFile = await Convert(etlFinalFile, processList.Select(x => x.Id).ToList(), ultraProfilerOptions);

        if (!ultraProfilerOptions.KeepMergedEtl)
        {
            File.Delete(etlFinalFile);
            var etlxFinalFile = Path.ChangeExtension(etlFinalFile, ".etlx");
            File.Delete(etlxFinalFile);
        }

        return jsonFinalFile;
    }

    private protected override Task EnableProfilingImpl(TraceEventProviderOptions options, EtwUltraProfilerOptions ultraProfilerOptions)
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
        _profilerClock.Restart();

        return Task.CompletedTask;
    }
}