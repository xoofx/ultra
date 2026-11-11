// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using Microsoft.Diagnostics.Symbols;

namespace Ultra.Core;

/// <summary>
/// Options for <see cref="UltraProfiler"/>.
/// </summary>
public class UltraProfilerOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UltraProfilerOptions"/> class with default values.
    /// </summary>
    public UltraProfilerOptions()
    {
        CheckDeltaTimeInMs = 500;
        UpdateLogAfterInMs = 1000;
        TimeOutAfterInMs = 30 * 1000; // 30s
        // The macOS sampler cannot sleep less than 1ms between samples
        CpuSamplingIntervalInMs = OperatingSystem.IsMacOS() ? 1.0f : 1000.0f / 8190.0f;
        KeepMergedEtl = false;
        KeepEtlIntermediateFiles = false;
        FastFilter = true;
        DelayInSeconds = 0.0; // 0 seconds
        DurationInSeconds = 120.0; // 120 seconds
        MinimumCpuTimeBeforeThreadIsVisibleInMs = 10.0;
    }

    /// <summary>
    /// Gets the list of process IDs to profile.
    /// </summary>
    public List<int> ProcessIds { get; } = new();

    /// <summary>
    /// Gets or sets the path to the program to profile.
    /// </summary>
    public string? ProgramPath { get; set; }

    /// <summary>
    /// Gets the list of arguments to pass to the program.
    /// </summary>
    public List<string> Arguments { get; } = new();

    /// <summary>
    /// Gets or sets the interval in milliseconds to check delta time.
    /// </summary>
    public int CheckDeltaTimeInMs { get; set; }

    /// <summary>
    /// Gets or sets the interval in milliseconds to update the log.
    /// </summary>
    public int UpdateLogAfterInMs { get; set; }

    /// <summary>
    /// Gets or sets the timeout in milliseconds after which profiling will stop.
    /// </summary>
    public int TimeOutAfterInMs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the profiling is paused.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>
    /// Gets or sets the delay in seconds before starting the profiling.
    /// </summary>
    public double DelayInSeconds { get; set; }

    /// <summary>
    /// Gets or sets the duration in seconds for which the profiling will run.
    /// </summary>
    public double DurationInSeconds { get; set; }

    /// <summary>
    /// Gets or sets the console mode for the profiler.
    /// </summary>
    public UltraProfilerConsoleMode ConsoleMode { get; set; }

    /// <summary>
    /// Gets or sets the action to log progress messages.
    /// </summary>
    public Action<string>? LogProgress;

    /// <summary>
    /// Gets or sets the action to log step progress messages.
    /// </summary>
    public Action<string>? LogStepProgress;

    /// <summary>
    /// Gets or sets the action to log a warning, kept on screen (progress messages are replaced by the next one).
    /// <see cref="LogProgress"/> is used when not set.
    /// </summary>
    public Action<string>? LogWarning;

    /// <summary>
    /// Gets or sets the action to log messages when waiting for a file to complete.
    /// </summary>
    public Action<string>? WaitingFileToComplete;

    /// <summary>
    /// Gets or sets the action to log messages when waiting for a file to complete times out.
    /// </summary>
    public Action<string>? WaitingFileToCompleteTimeOut;

    /// <summary>
    /// Gets or sets the action to log standard output from the program.
    /// </summary>
    public Action<string>? ProgramLogStdout;

    /// <summary>
    /// Gets or sets the action to log standard error output from the program.
    /// </summary>
    public Action<string>? ProgramLogStderr;

    /// <summary>
    /// Gets or sets an optional function that starts the program to profile (<see cref="ProgramPath"/> with <see cref="Arguments"/>)
    /// in place of the profiler, for example from another process that is not elevated. The function returns the started process,
    /// which must still be running. When set, <see cref="ConsoleMode"/>, <see cref="ProgramLogStdout"/> and <see cref="ProgramLogStderr"/>
    /// are not used: the output of the program is the responsibility of the function.
    /// </summary>
    public Func<ProcessStartInfo, Task<Process>>? ProgramStarter { get; set; }

    /// <summary>
    /// Gets or sets the function to determine whether profiling should start.
    /// </summary>
    public Func<bool>? ShouldStartProfiling { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to keep intermediate ETL files.
    /// </summary>
    public bool KeepEtlIntermediateFiles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to keep the merged ETL file.
    /// </summary>
    public bool KeepMergedEtl { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether only the CPU samples, stacks and context switches of the profiled processes
    /// are converted from an ETL file (see <see cref="UltraEtwFastFilter"/>). Default is true.
    /// </summary>
    public bool FastFilter { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the lock contention of the .NET processes is recorded (<c>--locks</c>): the runtime's
    /// contention events (a thread waiting for a <c>lock</c>, a <c>Monitor</c> or a <c>System.Threading.Lock</c> held by another),
    /// each wait a "Lock Contention" marker on the waiting thread, with the lock owner and, on Windows, the stack where it waited.
    /// </summary>
    public bool LockContention { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a GC heap snapshot of each .NET process is taken at the end of the profiling (see
    /// <see cref="UltraHeapSnapshot"/>): the live objects by retention path, as the <c>[heap]</c> and <c>[heap objects]</c> tracks.
    /// It stops the threads of the process for the heap walk.
    /// </summary>
    public bool HeapSnapshot { get; set; }

    /// <summary>
    /// Gets or sets how long to wait for the heap walk of a <see cref="HeapSnapshot"/>, in seconds. Default is 300.
    /// </summary>
    public double HeapSnapshotTimeoutInSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets the EventPipe buffer of a <see cref="HeapSnapshot"/> in the profiled process, in MB: events are lost when ultra
    /// reads them slower than the runtime writes them and the buffer is full. Default is 0: the private memory of the process (at least
    /// 256 MB, at most half the free memory of the machine), see <see cref="UltraHeapSnapshot.BufferSize"/>.
    /// </summary>
    public int HeapSnapshotBufferInMB { get; set; }

    /// <summary>
    /// Gets or sets the CPU sampling interval in milliseconds.
    /// </summary>
    public float CpuSamplingIntervalInMs { get; set; }

    /// <summary>
    /// Gets the hardware counters to sample in addition to the CPU time (Linux and Windows). On Linux, e.g. <c>cycles</c>,
    /// <c>cache-misses:10000</c>, <c>LLC-load-misses</c>, <c>r0843</c>; on Windows the ETW profile sources of the machine,
    /// e.g. <c>DcacheMisses</c>, <c>CacheMisses:65536</c> (see <see cref="UltraEtwHardwareCounter"/>). Each gives a track per thread.
    /// </summary>
    public List<string> HardwareCounters { get; } = new();

    /// <summary>
    /// Gets or sets whether each hardware counter sample (<see cref="HardwareCounters"/>) records its call stack (Windows only). Off by
    /// default: a counter sample is then the code address where the counter fired, a one-frame stack - the same counters per function,
    /// without the callers in the Firefox Profiler, for a fraction of the trace, of its conversion time and of the counter interrupts'
    /// cost in the app. Linux records the call chain of every sample.
    /// </summary>
    public bool HardwareCounterStacks { get; set; }

    /// <summary>
    /// Gets or sets the samples per second per CPU the kernel aims at for a hardware counter given without a period (Linux only). Default 4000.
    /// </summary>
    public int HardwareCounterFrequency { get; set; } = (int)Linux.PerfEventSpec.DefaultFrequency;

    /// <summary>
    /// Gets or sets the symbol path text.
    /// </summary>
    public string? SymbolPathText { get; set; }

    /// <summary>
    /// Gets or sets the seconds a symbol server gets to answer a request (Windows ETW conversion). A server that does not is
    /// skipped for the modules that follow, and the modules it was not asked for are looked up once more at the end.
    /// Default 5; 0 or less: no limit and no retry (a server that hangs then holds up every module it does not have).
    /// </summary>
    public double SymbolServerTimeoutInSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets the base output file name.
    /// </summary>
    public string? BaseOutputFileName { get; set; }

    /// <summary>
    /// Gets or sets the minimum CPU time before a thread is visible in milliseconds.
    /// </summary>
    public double MinimumCpuTimeBeforeThreadIsVisibleInMs { get; set; }

    /// <summary>
    /// Ensures that the directory for the base output file name exists.
    /// </summary>
    public void EnsureDirectoryForBaseOutputFileName()
    {
        if (BaseOutputFileName == null) return;

        var directory = Path.GetDirectoryName(BaseOutputFileName);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Gets the cached symbol path.
    /// </summary>
    /// <returns>The cached symbol path.</returns>
    public SymbolPath GetCachedSymbolPath()
    {
        var symbolPath = new SymbolPath();
        if (SymbolPathText != null)
        {
            symbolPath.Add(SymbolPathText);
        }
        else
        {
            var symbolPathText = SymbolPath.SymbolPathFromEnvironment;
            if (string.IsNullOrEmpty(symbolPathText))
            {
                symbolPathText = $"{SymbolPath.MicrosoftSymbolServerPath};SRV*https://symbols.nuget.org/download/symbols";
            }

            symbolPath.Add(symbolPathText);
        }

        return symbolPath.InsureHasCache(symbolPath.DefaultSymbolCache()).CacheFirst();
    }
}
