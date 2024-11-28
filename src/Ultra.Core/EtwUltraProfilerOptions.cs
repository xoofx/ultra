// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Symbols;

namespace Ultra.Core;

/// <summary>
/// Options for <see cref="EtwUltraProfiler"/>.
/// </summary>
public class EtwUltraProfilerOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EtwUltraProfilerOptions"/> class with default values.
    /// </summary>
    public EtwUltraProfilerOptions()
    {
        CheckDeltaTimeInMs = 500;
        UpdateLogAfterInMs = 1000;
        TimeOutAfterInMs = 30 * 1000; // 30s
        CpuSamplingIntervalInMs = 1000.0f / 8190.0f;
        KeepMergedEtl = false;
        KeepEtlIntermediateFiles = false;
        DelayInSeconds = 0.0; // 0 seconds
        DurationInSeconds = 120.0; // 120 seconds
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
    public EtwUltraProfilerConsoleMode ConsoleMode { get; set; }

    /// <summary>
    /// Gets or sets the action to log progress messages.
    /// </summary>
    public Action<string>? LogProgress;

    /// <summary>
    /// Gets or sets the action to log step progress messages.
    /// </summary>
    public Action<string>? LogStepProgress;

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
    /// Gets or sets the CPU sampling interval in milliseconds.
    /// </summary>
    public float CpuSamplingIntervalInMs { get; set; }

    /// <summary>
    /// Gets or sets the symbol path text.
    /// </summary>
    public string? SymbolPathText { get; set; }

    /// <summary>
    /// Gets or sets the base output file name.
    /// </summary>
    public string? BaseOutputFileName { get; set; }

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
