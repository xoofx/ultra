// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using ByteSizeLib;
using Microsoft.Diagnostics.Tracing.Session;

namespace Ultra.Core;

/// <summary>
/// A profiler that uses Event Tracing for Windows (ETW) to collect performance data.
/// </summary>
public abstract class UltraProfiler : IDisposable
{
    private protected bool CancelRequested;
    private protected ManualResetEvent? CleanCancel;
    private protected bool StopRequested;
    private protected readonly Stopwatch ProfilerClock;
    private protected TimeSpan LastTimeProgress;
    private readonly CancellationTokenSource _cancellationTokenSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="UltraProfiler"/> class.
    /// </summary>
    protected UltraProfiler()
    {
        ProfilerClock = new Stopwatch();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    protected CancellationToken CancellationToken => _cancellationTokenSource.Token;

    /// <summary>
    /// Creates a new instance of the <see cref="UltraProfiler"/> class.
    /// </summary>
    /// <returns>A new instance of the <see cref="UltraProfiler"/> class.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when the current platform is not supported.</exception>
    public static UltraProfiler Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new UltraProfilerEtw();
        }

        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return new UltraProfilerEventPipe();
        }

        throw new PlatformNotSupportedException("Only Windows or macOS+ARM64 are supported");
    }

    /// <summary>
    /// Requests to cancel the profiling session.
    /// </summary>
    /// <returns>True if the profiling session was already canceled; otherwise, false.</returns>
    public bool Cancel()
    {
        if (!CancelRequested)
        {
            CleanCancel = new ManualResetEvent(false);
            CancelRequested = true;
            return false;
        }
        else
        {
            StopRequested = true;

            // Before really canceling, wait for the clean cancel to be done
            WaitForCleanCancel();
            return true;
        }
    }

    /// <summary>
    /// Releases all resources used by the <see cref="UltraProfiler"/> class.
    /// </summary>
    public void Dispose()
    {
        DisposeImpl();
        CleanCancel?.Dispose();
        CleanCancel = null;
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
    public async Task<string> Run(UltraProfilerOptions ultraProfilerOptions)
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

        ProfilerClock.Restart();
        LastTimeProgress = ProfilerClock.Elapsed;

        var runner = CreateRunner(ultraProfilerOptions, processList, baseName, singleProcess);
        try
        {
            await runner.OnStart();
            {
                var startTheRequestedProgramIfRequired = async () =>
                {
                    // Start a command line process if needed
                    if (ultraProfilerOptions.ProgramPath is not null)
                    {
                        var processState = await StartProcess(runner, ultraProfilerOptions);
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
                // On macOS we always need to start the program before enabling profiling
                bool hasExplicitProgramHasStarted = ultraProfilerOptions.DelayInSeconds != 0.0 || ultraProfilerOptions.Paused || OperatingSystem.IsMacOS();
                if (hasExplicitProgramHasStarted)
                {
                    await startTheRequestedProgramIfRequired();
                }

                // Wait for the process to start
                if (ultraProfilerOptions.Paused)
                {
                    while (!ultraProfilerOptions.ShouldStartProfiling!() && !CancelRequested && !StopRequested)
                    {
                    }

                    // If we have a cancel request, we don't start the profiling
                    if (CancelRequested || StopRequested)
                    {
                        throw new InvalidOperationException("CTRL+C requested");
                    }
                }

                await EnableProfiling(runner, ultraProfilerOptions);

                // If we haven't started the program yet, we start it now (for explicit program path)
                if (!hasExplicitProgramHasStarted)
                {
                    await startTheRequestedProgramIfRequired();
                }

                foreach (var process in processList)
                {
                    ultraProfilerOptions.LogProgress?.Invoke($"Start Profiling Process {process.ProcessName} ({process.Id})");
                }

                // Collect the data until all processes have exited or there is a cancel request
                HashSet<Process> exitedProcessList = new();
                while (!CancelRequested)
                {
                    // Exit if we have reached the duration
                    if (ProfilerClock.Elapsed.TotalSeconds > ultraProfilerOptions.DurationInSeconds)
                    {
                        ultraProfilerOptions.LogProgress?.Invoke($"Stopping profiling, max duration reached at {ultraProfilerOptions.DurationInSeconds}s");
                        break;
                    }

                    if (ProfilerClock.Elapsed.TotalMilliseconds - LastTimeProgress.TotalMilliseconds > ultraProfilerOptions.UpdateLogAfterInMs)
                    {
                        var totalFileNameLength = runner.OnProfiling();

                        ultraProfilerOptions.LogStepProgress?.Invoke(singleProcess is not null
                            ? $"Profiling Process {singleProcess.ProcessName} ({singleProcess.Id}) - {(int)ProfilerClock.Elapsed.TotalSeconds}s - {ByteSize.FromBytes(totalFileNameLength)}"
                            : $"Profiling {processList.Count} Processes - {(int)ProfilerClock.Elapsed.TotalSeconds}s - {ByteSize.FromBytes(totalFileNameLength)}");
                        LastTimeProgress = ProfilerClock.Elapsed;
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

                ultraProfilerOptions.LogProgress?.Invoke(singleProcess is not null ? $"End Profiling Process" : $"End Profiling {processList.Count} Processes");

                await runner.OnStop();
            }
        }
        catch
        {
            await runner.OnCatch();
            throw;
        }
        finally
        {
            await runner.OnFinally();
            CleanCancel?.Set();
        }

        if (StopRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        var fileToConvert = await runner.FinishFileToConvert();

        string jsonFinalFile = string.Empty;
        if (!string.IsNullOrEmpty(fileToConvert))
        {
            jsonFinalFile = await Convert(fileToConvert, processList.Select(x => x.Id).ToList(), ultraProfilerOptions);
        }

        await runner.OnFinalCleanup();
        
        return jsonFinalFile;
    }

    
    private protected abstract ProfilerRunner CreateRunner(UltraProfilerOptions ultraProfilerOptions, List<Process> processList, string baseName, Process? singleProcess);

    /// <summary>
    /// Converts the ETL file to a compressed JSON file in the Firefox Profiler format.
    /// </summary>
    /// <param name="etlFile">The path to the ETL file.</param>
    /// <param name="pIds">The list of process IDs to include in the conversion.</param>
    /// <param name="ultraProfilerOptions">The options for the profiler.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the path to the generated JSON file.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a stop request is received.</exception>
    public async Task<string> Convert(string etlFile, List<int> pIds, UltraProfilerOptions ultraProfilerOptions)
    {
        var profile = ConverterToFirefox.Convert(etlFile, ultraProfilerOptions, pIds);

        if (StopRequested)
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

    private async Task EnableProfiling(ProfilerRunner runner, UltraProfilerOptions ultraProfilerOptions)
    {
        ProfilerClock.Restart();
        while (!CancelRequested)
        {
            var deltaInSecondsBeforeProfilingCanStart = ultraProfilerOptions.DelayInSeconds - ProfilerClock.Elapsed.TotalSeconds;

            if (deltaInSecondsBeforeProfilingCanStart <= 0)
            {
                break;
            }

            if (ProfilerClock.Elapsed.TotalMilliseconds - LastTimeProgress.TotalMilliseconds > ultraProfilerOptions.UpdateLogAfterInMs)
            {
                ultraProfilerOptions.LogStepProgress?.Invoke($"Delay before starting the profiler {deltaInSecondsBeforeProfilingCanStart:0.0}s");
                LastTimeProgress = ProfilerClock.Elapsed;
            }

            // We don't handle the case of the process being killed during the delay
            // As it complicates the check when we start the process right after enabling the profiling

            // The loop checking for the process to be killed will happen after anyway

            await Task.Delay(ultraProfilerOptions.CheckDeltaTimeInMs);
        }

        // In case of a cancel request during the delay, we assume that it is a CTRL+C and not a stop of the profiler, as we haven't started profiling yet
        if (CancelRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        await runner.OnEnablingProfiling();

        // Reset the clock to account for the duration of the profiler
        ProfilerClock.Restart();
    }
    
    private protected async Task WaitForStaleFile(string file, UltraProfilerOptions options)
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

    private protected static async Task<ProcessState> StartProcess(ProfilerRunner runner, UltraProfilerOptions ultraProfilerOptions)
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

        if (mode == UltraProfilerConsoleMode.Silent)
        {
            startInfo.UseShellExecute = true;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;

            if (runner.OnPrepareStartProcess != null)
            {
                await runner.OnPrepareStartProcess(startInfo);
            }

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

            if (runner.OnPrepareStartProcess != null)
            {
                await runner.OnPrepareStartProcess(startInfo);
            }

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

        if (runner.OnProcessStarted != null)
        {
            await runner.OnProcessStarted(process);
        }

        return state;
    }

    private void WaitForCleanCancel()
    {
        if (CleanCancel is not null)
        {
            CleanCancel.WaitOne();
            CleanCancel.Dispose();
            CleanCancel = null;
        }
    }

    private protected class ProfilerRunner(string baseFileName)
    {
        public string BaseFileName { get; } = baseFileName;

        public required Func<Task> OnStart;

        public required Func<Task> OnEnablingProfiling;

        public required Func<long> OnProfiling;

        public required Func<Task> OnStop;

        public Func<ProcessStartInfo, Task>? OnPrepareStartProcess;

        public Func<Process, Task>? OnProcessStarted;

        public required Func<Task> OnCatch;

        public required Func<Task> OnFinally;

        public required Func<Task<string>> FinishFileToConvert;

        public required Func<Task> OnFinalCleanup;
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