// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ByteSizeLib;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Ultra.Core.Automation;

namespace Ultra.Mcp;

/// <summary>
/// The capture tools of the MCP server: profiling and conversion run as jobs, each one an ultra command line process
/// (<c>ultra profile --result-file ... --stop-file ...</c>) that the agent polls and stops.
/// </summary>
[McpServerToolType]
internal static class UltraJobs
{
    /// <summary>The maximum wait of one tool call, to stay below the request timeouts of the MCP clients.</summary>
    public const double MaxWaitSeconds = 300;

    private const int MaxOutputLines = 1000;

    private static readonly Lock JobsLock = new();
    private static readonly Dictionary<int, UltraJob> Jobs = new();
    private static UltraSelfCommand? _selfCommand;
    private static int _nextJobId;

    /// <summary>
    /// Sets how to start ultra for the jobs.
    /// </summary>
    /// <param name="selfCommand">The ultra command.</param>
    public static void Configure(UltraSelfCommand selfCommand)
    {
        ArgumentNullException.ThrowIfNull(selfCommand);
        _selfCommand = selfCommand;
    }

    [McpServerTool(Name = "start_profiling", OpenWorld = true)]
    [Description(@"Starts a CPU profiling capture with ultra, as a background job, and returns its jobId at once. Either attach to running processes (pids) or launch a program (program + arguments) to profile it from its start.

The capture ends when the duration is reached, when all the profiled processes exit, or when stop_job is called; ultra then converts the trace (a few seconds to a minute) and writes one Firefox Profiler .json.gz file per process. Follow the job with get_job_status (use waitSeconds to wait for its end): its result lists the profile files to analyze with get_profile_report, load_profile, call_tree...

Windows: profiling needs administrator rights. With elevate=true (default) and an MCP server that is not elevated, Windows shows a UAC prompt to the user, who must accept it: tell the user to expect it. A launched program still runs as the user (not elevated).
Linux: needs root or kernel.perf_event_paranoid <= 0. macOS (ARM64): launch only, no pids.")]
    public static string StartProfiling(
        [Description("The ids of running processes to attach to (find them with find_processes). Leave empty to launch a program instead.")] int[]? pids = null,
        [Description("The program to launch and profile from its start (on Windows a path to an .exe, e.g. C:\\path\\app.exe or dotnet.exe). Ignored when pids are given.")] string? program = null,
        [Description("The arguments of the launched program.")] string[]? arguments = null,
        [Description("The working directory of ultra and of the launched program. The profiles are written there, and a relative output is relative to it. Default: the ultra profiles folder (%LOCALAPPDATA%\\ultra\\profiles on Windows, ~/.local/share/ultra/profiles elsewhere).")] string? workingDirectory = null,
        [Description("The maximum duration of the capture in seconds. Default 60. Keep it short: a few seconds of a slow operation are enough, and the profile size grows with it.")] double durationSeconds = 60,
        [Description("A delay in seconds before the capture starts (the program is launched at once). Default 0.")] double delaySeconds = 0,
        [Description("The sampling interval in milliseconds. Default: 0.122 ms (8190 Hz) on Windows, 1 ms on macOS.")] double? samplingIntervalMs = null,
        [Description("The base output file name (absolute, or relative to the working directory), without extension. Default: ultra_<process>_<date>_pid_<pid> in the working directory.")] string? output = null,
        [Description("Windows only: when ultra is not already elevated, ask the user for administrator rights through a UAC prompt. Default true.")] bool elevate = true)
        => UltraTools.Run(() =>
        {
            var args = new List<string> { "profile" };
            if (pids is { Length: > 0 })
            {
                args.Add("--pid");
                args.Add(string.Join(",", pids));
            }
            else if (string.IsNullOrWhiteSpace(program))
            {
                throw new McpException("Either pids or program is required. Use find_processes to find the pid of a running process.");
            }

            args.Add("--duration");
            args.Add(durationSeconds.ToString(CultureInfo.InvariantCulture));
            if (delaySeconds > 0)
            {
                args.Add("--delay");
                args.Add(delaySeconds.ToString(CultureInfo.InvariantCulture));
            }

            if (samplingIntervalMs is { } interval)
            {
                args.Add("--sampling-interval");
                args.Add(interval.ToString(CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(output))
            {
                args.Add("--output");
                args.Add(output);
            }

            if (elevate && OperatingSystem.IsWindows())
            {
                args.Add("--elevate");
            }

            if (pids is not { Length: > 0 })
            {
                args.Add("--");
                // A relative path is relative to the MCP server, not to the working directory of the job
                args.Add(program!.IndexOfAny(['/', '\\']) >= 0 ? Path.GetFullPath(program) : program);
                args.AddRange(arguments ?? []);
            }

            // Not the working directory of the MCP server, which is often the repository of the agent
            workingDirectory ??= GetDefaultProfilesDirectory();
            return Describe(StartJob("profile", args, workingDirectory), 0);
        });

    [McpServerTool(Name = "start_conversion", OpenWorld = true)]
    [Description(@"Converts an existing trace to Firefox Profiler .json.gz files, as a background job, and returns its jobId at once: a Windows .etl file (e.g. kept with --keep-merged-etl-file), a Linux .ultraperf file with its CLR .nettrace files, or the macOS sampler/clr .nettrace files. Follow it with get_job_status. Does not need administrator rights.")]
    public static string StartConversion(
        [Description("The trace files: one .etl, or one .ultraperf (and optionally its _clr.nettrace files), or the _sampler.nettrace and _clr.nettrace files.")] string[] traceFiles,
        [Description("The ids of the processes to convert: each one gets its own .json.gz file.")] int[] pids,
        [Description("The working directory of the conversion, where the profiles are written. Default: the folder of the first trace file.")] string? workingDirectory = null,
        [Description("The base output file name, without extension. Default: the trace file name without its extension.")] string? output = null)
        => UltraTools.Run(() =>
        {
            if (traceFiles is not { Length: > 0 })
            {
                throw new McpException("traceFiles is required");
            }

            if (pids is not { Length: > 0 })
            {
                throw new McpException("pids is required: the ids of the processes to convert from the trace");
            }

            var args = new List<string> { "convert", "--pid", string.Join(",", pids) };
            if (!string.IsNullOrWhiteSpace(output))
            {
                args.Add("--output");
                args.Add(output);
            }

            // By default the profiles are written next to the trace
            var fullTraceFiles = traceFiles.Select(Path.GetFullPath).ToList();
            args.AddRange(fullTraceFiles);
            return Describe(StartJob("convert", args, workingDirectory ?? Path.GetDirectoryName(fullTraceFiles[0])), 0);
        });

    [McpServerTool(Name = "get_job_status", ReadOnly = true)]
    [Description(@"Returns the state of a capture or conversion job (running / succeeded / failed), its last output lines and, once it has ended, the profile files written. Use waitSeconds to wait for the end of the job (at most 300 s per call; call again while it is running).")]
    public static async Task<string> GetJobStatus(
        [Description("The id returned by start_profiling or start_conversion.")] int jobId,
        [Description("How long to wait for the end of the job, in seconds (0 = do not wait, max 300).")] double waitSeconds = 0,
        [Description("How many of the last output lines of ultra to return. Default 20.")] int outputLines = 20,
        CancellationToken cancellationToken = default)
    {
        var job = GetJob(jobId);
        await WaitForExitAsync(job, waitSeconds, cancellationToken).ConfigureAwait(false);
        return Describe(job, outputLines);
    }

    [McpServerTool(Name = "stop_job", Destructive = true)]
    [Description(@"Stops a capture job before its duration: ultra stops recording, still converts the trace and writes the profile files (so wait for the end with waitSeconds). force=true kills ultra instead, which loses the capture. A conversion job is killed.")]
    public static async Task<string> StopJob(
        [Description("The id returned by start_profiling or start_conversion.")] int jobId,
        [Description("How long to wait for the end of the job, in seconds (max 300). Default 120: the conversion after the stop takes a few seconds to a minute.")] double waitSeconds = 120,
        [Description("Kill ultra instead of asking it to stop (the capture is lost). Default false.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var job = GetJob(jobId);
        if (!job.Process.HasExited)
        {
            if (force || job.StopFile is null)
            {
                job.Process.Kill(entireProcessTree: true);
            }
            else
            {
                job.RequestStop();
            }
        }

        await WaitForExitAsync(job, waitSeconds, cancellationToken).ConfigureAwait(false);
        return Describe(job, 20);
    }

    [McpServerTool(Name = "list_jobs", ReadOnly = true, Idempotent = true)]
    [Description("Lists the capture and conversion jobs started by this MCP server, one per line: 'jobId kind state elapsed command'.")]
    public static string ListJobs() => UltraTools.Run(() =>
    {
        UltraJob[] jobs;
        lock (JobsLock)
        {
            jobs = Jobs.Values.OrderBy(x => x.Id).ToArray();
        }

        if (jobs.Length == 0)
        {
            return "no jobs";
        }

        return string.Join("\n", jobs.Select(job => $"{job.Id}\t{job.Kind}\t{job.State}\t{job.Elapsed.TotalSeconds:0.0}s\t{job.CommandLine}"));
    });

    [McpServerTool(Name = "find_processes", ReadOnly = true, Idempotent = true)]
    [Description("Lists the running processes whose name or main window title contains a text (case-insensitive), to find the pids to profile with start_profiling. One per line: 'pid name started=... title=...', most recently started first.")]
    public static string FindProcesses(
        [Description("A case-insensitive part of the process name or of its main window title, e.g. 'dotnet' or 'MyApp'. Empty lists all the processes.")] string? nameContains = null,
        [Description("The maximum number of processes to list. Default 50.")] int maxResults = 50)
        => UltraTools.Run(() =>
        {
            var lines = new List<(DateTime Start, string Line)>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        var name = process.ProcessName;
                        var title = OperatingSystem.IsWindows() ? process.MainWindowTitle : string.Empty;
                        if (!string.IsNullOrEmpty(nameContains) &&
                            !name.Contains(nameContains, StringComparison.OrdinalIgnoreCase) &&
                            !title.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        DateTime start = DateTime.MinValue;
                        try
                        {
                            start = process.StartTime;
                        }
                        catch (Exception)
                        {
                            // Access denied for some system processes
                        }

                        var line = new StringBuilder();
                        line.Append(process.Id).Append('\t').Append(name);
                        if (start != DateTime.MinValue)
                        {
                            line.Append("\tstarted=").Append(start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        }

                        if (!string.IsNullOrEmpty(title))
                        {
                            line.Append("\ttitle=").Append(title);
                        }

                        lines.Add((start, line.ToString()));
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited meanwhile
                    }
                }
            }

            if (lines.Count == 0)
            {
                return string.IsNullOrEmpty(nameContains) ? "no processes" : $"no process matching '{nameContains}'";
            }

            var take = Math.Clamp(maxResults, 1, UltraTools.MaxAllowedResults);
            var result = lines.OrderByDescending(x => x.Start).Take(take).Select(x => x.Line).ToList();
            var header = $"({result.Count} of {lines.Count} processes)";
            return header + "\n" + string.Join("\n", result);
        });

    /// <summary>
    /// Gets (and creates) the folder where the profiling jobs write their profiles when no working directory is given:
    /// <c>%LOCALAPPDATA%\ultra\profiles</c> on Windows, <c>~/.local/share/ultra/profiles</c> on Linux and macOS.
    /// </summary>
    internal static string GetDefaultProfilesDirectory()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "ultra", "profiles");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Requests the running jobs to stop (when the server exits): the captures still write their profile.
    /// </summary>
    public static void StopAll()
    {
        lock (JobsLock)
        {
            foreach (var job in Jobs.Values)
            {
                if (!job.Process.HasExited)
                {
                    job.RequestStop();
                }
            }
        }
    }

    /// <summary>
    /// Starts an ultra command line as a job.
    /// </summary>
    internal static UltraJob StartJob(string kind, List<string> arguments, string? workingDirectory)
    {
        var selfCommand = _selfCommand ?? throw new McpException("The ultra command used to run jobs is not configured");

        int id;
        lock (JobsLock)
        {
            id = ++_nextJobId;
        }

        var jobDirectory = Path.Combine(Path.GetTempPath(), "ultra", "mcp", $"{Environment.ProcessId}_job{id}");
        Directory.CreateDirectory(jobDirectory);
        var resultFile = Path.Combine(jobDirectory, "result.json");
        // Only a capture can stop and still write its profile: a conversion is killed by stop_job
        var stopFile = kind == "profile" ? Path.Combine(jobDirectory, "stop") : null;

        // The automation options go right after the command name (`profile`/`convert`), before a `--` and its program arguments
        var allArguments = new List<string>(arguments);
        allArguments.InsertRange(1, stopFile is null ? ["--result-file", resultFile] : ["--result-file", resultFile, "--stop-file", stopFile]);

        var startInfo = selfCommand.CreateStartInfo(allArguments);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            if (!Directory.Exists(workingDirectory))
            {
                throw new McpException($"The working directory {workingDirectory} does not exist");
            }

            startInfo.WorkingDirectory = workingDirectory;
        }

        var job = new UltraJob(id, kind, SelfCommandLine(arguments), resultFile, stopFile, new Process { StartInfo = startInfo, EnableRaisingEvents = true });
        job.Process.OutputDataReceived += (_, e) => job.AddOutput(e.Data);
        job.Process.ErrorDataReceived += (_, e) => job.AddOutput(e.Data);
        job.Process.Exited += (_, _) => job.MarkExited();
        job.Process.Start();
        job.Process.StandardInput.Close();
        job.Process.BeginOutputReadLine();
        job.Process.BeginErrorReadLine();

        lock (JobsLock)
        {
            Jobs.Add(id, job);
        }

        return job;
    }

    private static string SelfCommandLine(IEnumerable<string> arguments) => UltraSelfCommand.FormatCommandLine("ultra", arguments);

    internal static UltraJob GetJob(int jobId)
    {
        lock (JobsLock)
        {
            if (Jobs.TryGetValue(jobId, out var job))
            {
                return job;
            }
        }

        throw new McpException($"Unknown jobId {jobId}. Call list_jobs to see the jobs.");
    }

    private static async Task WaitForExitAsync(UltraJob job, double waitSeconds, CancellationToken cancellationToken)
    {
        if (waitSeconds <= 0 || job.Process.HasExited)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(waitSeconds, MaxWaitSeconds)));
        try
        {
            await job.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Still running after the wait
        }
    }

    /// <summary>
    /// Describes a job for the agent: state, result, last output lines and what to do next.
    /// </summary>
    internal static string Describe(UltraJob job, int outputLines)
    {
        var builder = new StringBuilder();
        var state = job.State;
        builder.Append("jobId: ").Append(job.Id).AppendLine();
        builder.Append("kind: ").AppendLine(job.Kind);
        builder.Append("state: ").AppendLine(state);
        builder.Append("command: ").AppendLine(job.CommandLine);
        builder.Append("elapsed: ").Append(job.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).AppendLine("s");
        if (job.StopRequested)
        {
            builder.AppendLine("stopRequested: true");
        }

        var result = job.Process.HasExited ? UltraCommandResult.TryRead(job.ResultFile) : null;
        if (job.Process.HasExited)
        {
            builder.Append("exitCode: ").Append(job.Process.ExitCode).AppendLine();
        }

        if (result is not null)
        {
            if (result.Error is not null)
            {
                builder.Append("error: ").AppendLine(result.Error);
            }

            if (result.Profiles.Count > 0)
            {
                builder.AppendLine("profiles:");
                foreach (var file in result.Profiles)
                {
                    builder.Append("  ").Append(file.Path).Append(" (").Append(ByteSize.FromBytes(file.Size).ToString()).AppendLine(")");
                }
            }
        }

        var lines = job.GetLastOutput(Math.Clamp(outputLines, 0, MaxOutputLines), out var totalLines);
        if (lines.Count > 0)
        {
            builder.Append("output (last ").Append(lines.Count).Append(" of ").Append(totalLines).AppendLine(" lines):");
            foreach (var line in lines)
            {
                builder.Append("  ").AppendLine(line);
            }
        }

        builder.Append("next: ");
        if (!job.Process.HasExited)
        {
            builder.Append(job.Kind == "profile"
                ? "call get_job_status with waitSeconds to wait for the end of the capture, or stop_job to stop it now (the profile is still written)."
                : "call get_job_status with waitSeconds to wait for the end of the conversion.");
            if (job.Kind == "profile" && OperatingSystem.IsWindows() && job.CommandLine.Contains("--elevate", StringComparison.Ordinal))
            {
                builder.Append(" If it waits for elevation, the user must accept the Windows UAC prompt.");
            }
        }
        else if (result is { Success: true, Profiles.Count: > 0 })
        {
            builder.Append("analyze the profile(s): get_profile_report path=<file> for an overview, then load_profile / list_threads / call_tree.");
        }
        else
        {
            builder.Append("the job failed: read the error and the output above, fix the arguments and start a new job.");
        }

        return builder.ToString();
    }

    /// <summary>
    /// A capture or conversion job: an ultra process with its output.
    /// </summary>
    internal sealed class UltraJob(int id, string kind, string commandLine, string resultFile, string? stopFile, Process process)
    {
        private readonly Queue<string> _output = new();
        private int _totalOutputLines;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _elapsedTicksAtExit = -1;

        public int Id { get; } = id;

        public string Kind { get; } = kind;

        public string CommandLine { get; } = commandLine;

        public string ResultFile { get; } = resultFile;

        public string? StopFile { get; } = stopFile;

        public Process Process { get; } = process;

        public bool StopRequested { get; private set; }

        public string State => !Process.HasExited ? "running" : Process.ExitCode == 0 ? "succeeded" : "failed";

        /// <summary>
        /// The time since the start of the job, frozen when it exits. Measured with a clock of its own: on macOS,
        /// <see cref="System.Diagnostics.Process.StartTime"/> throws once the process has exited.
        /// </summary>
        public TimeSpan Elapsed
        {
            get
            {
                if (Process.HasExited)
                {
                    MarkExited();
                }

                var ticksAtExit = Interlocked.Read(ref _elapsedTicksAtExit);
                return ticksAtExit >= 0 ? TimeSpan.FromTicks(ticksAtExit) : _clock.Elapsed;
            }
        }

        /// <summary>
        /// Freezes <see cref="Elapsed"/> (called when the process exits, or when its exit is first seen).
        /// </summary>
        public void MarkExited() => Interlocked.CompareExchange(ref _elapsedTicksAtExit, _clock.Elapsed.Ticks, -1);

        public void RequestStop()
        {
            StopRequested = true;
            if (StopFile is null)
            {
                Process.Kill(entireProcessTree: true);
            }
            else
            {
                File.WriteAllText(StopFile, "stop");
            }
        }

        public void AddOutput(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_output)
            {
                _output.Enqueue(line);
                _totalOutputLines++;
                if (_output.Count > MaxOutputLines)
                {
                    _output.Dequeue();
                }
            }
        }

        public List<string> GetLastOutput(int count, out int totalLines)
        {
            lock (_output)
            {
                totalLines = _totalOutputLines;
                return _output.Skip(Math.Max(0, _output.Count - count)).ToList();
            }
        }
    }
}
