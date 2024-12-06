// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Session;

namespace Ultra.Core;

/// <summary>
/// A profiler that uses Event Tracing for Windows (ETW) to collect performance data.
/// </summary>
public abstract class EtwUltraProfiler : IDisposable
{
    private protected bool _cancelRequested;
    private protected ManualResetEvent? _cleanCancel;
    private protected bool _stopRequested;
    private protected readonly Stopwatch _profilerClock;
    private protected TimeSpan _lastTimeProgress;

    /// <summary>
    /// Initializes a new instance of the <see cref="EtwUltraProfiler"/> class.
    /// </summary>
    protected EtwUltraProfiler()
    {
        _profilerClock = new Stopwatch();
    }

    /// <summary>
    /// Creates a new instance of the <see cref="EtwUltraProfiler"/> class.
    /// </summary>
    /// <returns>A new instance of the <see cref="EtwUltraProfiler"/> class.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when the current platform is not supported.</exception>
    public static EtwUltraProfiler Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new EtwUltraProfilerWindows();
        }

        throw new PlatformNotSupportedException("Only Windows is supported");
    }

    /// <summary>
    /// Requests to cancel the profiling session.
    /// </summary>
    /// <returns>True if the profiling session was already canceled; otherwise, false.</returns>
    public bool Cancel()
    {
        if (!_cancelRequested)
        {
            _cleanCancel = new ManualResetEvent(false);
            _cancelRequested = true;
            return false;
        }
        else
        {
            _stopRequested = true;

            // Before really canceling, wait for the clean cancel to be done
            WaitForCleanCancel();
            return true;
        }
    }

    /// <summary>
    /// Releases all resources used by the <see cref="EtwUltraProfiler"/> class.
    /// </summary>
    public void Dispose()
    {
        DisposeImpl();
        _cleanCancel?.Dispose();
        _cleanCancel = null;
    }

    private protected abstract void DisposeImpl();
    
    /// <summary>
    /// Determines whether the current process is running with elevated privileges.
    /// </summary>
    /// <returns>True if the current process is running with elevated privileges; otherwise, false.</returns>
    public static bool IsElevated()
    {
        var isElevated = TraceEventSession.IsElevated();
        return isElevated.HasValue && isElevated.Value;
    }

    /// <summary>
    /// Runs the profiler with the specified options.
    /// </summary>
    /// <param name="ultraProfilerOptions">The options for the profiler.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the path to the generated JSON file.</returns>
    /// <exception cref="ArgumentException">Thrown when the options are invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a cancel request is received.</exception>
    public async Task<string> Run(EtwUltraProfilerOptions ultraProfilerOptions)
    {
        if (ultraProfilerOptions.Paused && ultraProfilerOptions.ShouldStartProfiling is null)
        {
            throw new ArgumentException("ShouldStartProfiling is required when Paused is set to true");
        }

        if (ultraProfilerOptions.DurationInSeconds < 0)
        {
            throw new ArgumentException("DurationInSeconds must be greater or equal to 0");
        }

        if (ultraProfilerOptions.DelayInSeconds < 0)
        {
            throw new ArgumentException("DelayInSeconds must be greater or equal to 0");
        }

        List<System.Diagnostics.Process> processList = new List<System.Diagnostics.Process>();
        if (ultraProfilerOptions.ProcessIds.Count > 0)
        {
            foreach (var pidToAttach in ultraProfilerOptions.ProcessIds)
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(pidToAttach);
                    processList.Add(process);
                }
                catch (ArgumentException)
                {
                    throw new ArgumentException($"Unable to find Process with pid {pidToAttach}");
                }
            }
        }

        if (processList.Count == 0 && ultraProfilerOptions.ProgramPath is null)
        {
            throw new ArgumentException("pid is required or an executable with optional arguments");
        }

        string? processName = null;

        System.Diagnostics.Process? singleProcess = null;

        if (processList.Count == 1 && ultraProfilerOptions.ProgramPath is null)
        {
            singleProcess = processList[0];
            processName = singleProcess.ProcessName;
        }
        else if (ultraProfilerOptions.ProgramPath != null)
        {
            if (!ultraProfilerOptions.ProgramPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Executable path {ultraProfilerOptions.ProgramPath} must end with .exe");
            }

            processName = Path.GetFileNameWithoutExtension(ultraProfilerOptions.ProgramPath);
        }

        var currentTime = DateTime.Now;
        var baseName = processName != null ? $"ultra_{processName}_{currentTime:yyyy-MM-dd_HH_mm_ss}" : $"ultra_{currentTime:yyyy-MM-dd_HH_mm_ss}";

        // Append the pid for a single process that we are attaching to
        if (singleProcess is not null)
        {
            baseName = $"{baseName}_pid_{singleProcess.Id}";
        }

        var jsonFinalFile = await RunImpl(ultraProfilerOptions, processList, baseName, singleProcess);
        return jsonFinalFile;
    }

    private protected abstract Task<string> RunImpl(EtwUltraProfilerOptions ultraProfilerOptions, List<Process> processList, string baseName, Process? singleProcess);

    /// <summary>
    /// Converts the ETL file to a compressed JSON file in the Firefox Profiler format.
    /// </summary>
    /// <param name="etlFile">The path to the ETL file.</param>
    /// <param name="pIds">The list of process IDs to include in the conversion.</param>
    /// <param name="ultraProfilerOptions">The options for the profiler.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the path to the generated JSON file.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a stop request is received.</exception>
    public async Task<string> Convert(string etlFile, List<int> pIds, EtwUltraProfilerOptions ultraProfilerOptions)
    {
        var profile = EtwConverterToFirefox.Convert(etlFile, ultraProfilerOptions, pIds);

        if (_stopRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        var directory = Path.GetDirectoryName(etlFile);
        var etlFileNameWithoutExtension = Path.GetFileNameWithoutExtension(etlFile);
        var jsonFinalFile = $"{ultraProfilerOptions.BaseOutputFileName ?? etlFileNameWithoutExtension}.json.gz";
        ultraProfilerOptions.LogProgress?.Invoke($"Converting to Firefox Profiler JSON");
        await using var stream = File.Create(jsonFinalFile);
        await using var gzipStream = new GZipStream(stream, CompressionLevel.Optimal);
        await JsonSerializer.SerializeAsync(gzipStream, profile, FirefoxProfiler.JsonProfilerContext.Default.Profile);
        gzipStream.Flush();

        return jsonFinalFile;
    }

    private protected abstract Task EnableProfilingImpl(TraceEventProviderOptions options, EtwUltraProfilerOptions ultraProfilerOptions);

    private protected async Task EnableProfiling(TraceEventProviderOptions options, EtwUltraProfilerOptions ultraProfilerOptions)
    {
        _profilerClock.Restart();
        while (!_cancelRequested)
        {
            var deltaInSecondsBeforeProfilingCanStart = ultraProfilerOptions.DelayInSeconds - _profilerClock.Elapsed.TotalSeconds;

            if (deltaInSecondsBeforeProfilingCanStart <= 0)
            {
                break;
            }

            if (_profilerClock.Elapsed.TotalMilliseconds - _lastTimeProgress.TotalMilliseconds > ultraProfilerOptions.UpdateLogAfterInMs)
            {
                ultraProfilerOptions.LogStepProgress?.Invoke($"Delay before starting the profiler {deltaInSecondsBeforeProfilingCanStart:0.0}s");
                _lastTimeProgress = _profilerClock.Elapsed;
            }

            // We don't handle the case of the process being killed during the delay
            // As it complicates the check when we start the process right after enabling the profiling

            // The loop checking for the process to be killed will happen after anyway

            await Task.Delay(ultraProfilerOptions.CheckDeltaTimeInMs);
        }

        // In case of a cancel request during the delay, we assume that it is a CTRL+C and not a stop of the profiler, as we haven't started profiling yet
        if (_cancelRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        await EnableProfilingImpl(options, ultraProfilerOptions);

        // Reset the clock to account for the duration of the profiler
        _profilerClock.Restart();
    }


    private protected async Task WaitForStaleFile(string file, EtwUltraProfilerOptions options)
    {
        var clock = Stopwatch.StartNew();
        var startTime = clock.ElapsedMilliseconds;
        var fileInfo = new FileInfo(file);
        if (!fileInfo.Exists) return;
        var length = 0L;
        long lastTimeLogInMs = -1;
        while (true)
        {
            fileInfo.Refresh();
            var newLength = fileInfo.Length;
            if (newLength != length)
            {
                length = newLength;
            }
            else
            {
                break;
            }

            if (lastTimeLogInMs < 0 || (clock.ElapsedMilliseconds - lastTimeLogInMs) > options.UpdateLogAfterInMs)
            {
                options.WaitingFileToComplete?.Invoke(file);
                lastTimeLogInMs = clock.ElapsedMilliseconds;
            }

            if (clock.ElapsedMilliseconds - startTime > options.TimeOutAfterInMs)
            {
                options.WaitingFileToCompleteTimeOut?.Invoke(file);
                break;
            }

            await Task.Delay(options.CheckDeltaTimeInMs);
        }
    }

    private protected static ProcessState StartProcess(EtwUltraProfilerOptions ultraProfilerOptions)
    {
        var mode = ultraProfilerOptions.ConsoleMode;

        var process = new Process();

        var startInfo = process.StartInfo;
        startInfo.FileName = ultraProfilerOptions.ProgramPath;

        foreach (var arg in ultraProfilerOptions.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        ultraProfilerOptions.LogProgress?.Invoke($"Starting Process {startInfo.FileName} {string.Join(" ", startInfo.ArgumentList)}");

        if (mode == EtwUltraProfilerConsoleMode.Silent)
        {
            startInfo.UseShellExecute = true;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;

            process.Start();
        }
        else
        {
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardInput = true;

            process.OutputDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    ultraProfilerOptions.ProgramLogStdout?.Invoke(args.Data);
                }
            };

            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    ultraProfilerOptions.ProgramLogStderr?.Invoke(args.Data);
                }
            };

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        var state = new ProcessState(process);

        // Make sure to call WaitForExit
        var thread = new Thread(() =>
        {
            try
            {
                process.WaitForExit();
                state.HasExited = true;
            }
            catch
            {
                // ignore
            }
        })
        {
            Name = "Ultra-ProcessWaitForExit",
            IsBackground = true
        };
        thread.Start();

        return state;
    }

    private void WaitForCleanCancel()
    {
        if (_cleanCancel is not null)
        {
            _cleanCancel.WaitOne();
            _cleanCancel.Dispose();
            _cleanCancel = null;
        }
    }

    private protected class ProcessState
    {
        public ProcessState(Process process)
        {
            Process = process;
        }

        public readonly Process Process;

        public bool HasExited;
    }
}