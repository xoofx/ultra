// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.
using System.Diagnostics;
using System.Reflection;
using System.Text;
using ByteSizeLib;
using Ultra.Core;
using Ultra.Core.Automation;
using Ultra.Core.Linux;
using Ultra.Core.Report;
using Ultra.Mcp;
using XenoAtom.CommandLine;
using XenoAtom.Terminal;

namespace Ultra;

/// <summary>
/// Main entry point for the ultra command line.
/// </summary>
internal static class Program
{
    private const string _ = "";

    static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        return await RunAsync(args);
    }

    /// <summary>
    /// Gets the version of ultra.
    /// </summary>
    public static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>
    /// Runs the ultra command line (also used by the elevated ultra of `--elevate` for the command it is asked to run).
    /// </summary>
    private static async Task<int> RunAsync(string[] args)
    {
        List<int> pidList = new();

        bool verbose = false;
        bool elevate = false;
        string? resultFile = null;
        string? stopFile = null;
        int reportTop = 12, reportWorkers = 0;
        bool reportOffsets = false;
        string? reportThread = null;
        var options = new UltraProfilerOptions();

        var commandApp = new CommandApp("ultra", "Profile an application")
        {
            new CommandUsage("Usage: {NAME} [Options] command"),
            _,
            new HelpOption(),
            new VersionOption(),
            { "verbose", "Display verbose progress", v => verbose = v is not null },
            _,
            "Available commands:",
            new Command("profile", "Profile a new process or attach to an existing process")
            {
                new CommandUsage("Usage: {NAME} [Options] <pid | -- execName arg0 arg1...>"),
                _,
                new HelpOption(),
                { "o|output=", "The base output {FILE} name. Default is ultra_<process_name>_yyyy-MM-dd_HH_mm_ss.", v => options.BaseOutputFileName = v },
                { "pid=", "The {PID} of the process to attach the profiler to. Several pids can be separated by commas (or the option repeated): each process gets its own json file.", v => AddPids(pidList, v) },
                { "sampling-interval=", $"The {{VALUE}} of the sample interval in ms. Default is {options.CpuSamplingIntervalInMs:0.000}ms{(OperatingSystem.IsMacOS() ? ", minimum is 1ms on macOS" : " (8190Hz)")}.", (float v) => options.CpuSamplingIntervalInMs  = v },
                { "symbol-path=", $"The {{VALUE}} of symbol path. The default value is `{options.GetCachedSymbolPath()}`.", v => options.SymbolPathText  = v },
                { "pmc=", $"Linux only: hardware counters to sample besides the CPU time, comma separated {{NAME}}[:PERIOD] (default: adaptive 4000 samples/s per CPU). Each counter adds a `[name]` track per thread, weighted by the counts. Names, {PerfEventSpec.HelpText}", v => options.HardwareCounters.AddRange((v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) },
                { "pmc-frequency=", $"Linux only: the samples per second per CPU the kernel aims at for a --pmc counter given without a period. Default is {options.HardwareCounterFrequency}.", (int v) => options.HardwareCounterFrequency = v },
                { "paused", "Launch the profiler paused and wait for SPACE or ENTER keys to be pressed.", v => options.Paused = v is not null },
                { "delay=", $"Starts profiling after a specific delay (seconds). Default is {options.DelayInSeconds}s.", (double delay) => options.DelayInSeconds = delay },
                { "duration=", $"Run the profiler for a maximum duration (seconds). Default is {options.DurationInSeconds}s", (double duration) => options.DurationInSeconds = duration },
                { "keep-merged-etl-file", "Keep the merged ETL file. Without it (and without --nofilter) the ETL files are not merged: the profile is converted from the unmerged files directly.", v => options.KeepMergedEtl = v is not null },
                { "keep-intermediate-etl-files", "Keep the intermediate trace files: the ETL files before merging, or on Linux the .ultraperf and CLR .nettrace files.", v => options.KeepEtlIntermediateFiles = v is not null },
                { "nofilter", "Windows only: convert the full merged ETL file. By default only the CPU samples, stacks and context switches of the profiled process(es) are converted, which is much faster on a busy machine.", v => options.FastFilter = v is null },
                { "mode=", "Defines how the stdout/stderr of a program explicitly started by ultra should be integrated in its output. Default is `silent` which will not mix program's output. The other options are: `raw` is going to mix ultra and program output together in a raw output. `live` is going to mix ultra and program output within a live table.", v =>
                    {
                        if ("raw".Equals(v, StringComparison.OrdinalIgnoreCase))
                        {
                            options.ConsoleMode = UltraProfilerConsoleMode.Raw;
                        }
                        else if ("live".Equals(v, StringComparison.OrdinalIgnoreCase))
                        {
                            options.ConsoleMode = UltraProfilerConsoleMode.Live;
                        }
                        else
                        {
                            options.ConsoleMode = UltraProfilerConsoleMode.Silent;
                        }
                    }
                },
                { "<>", "Exec to launch with arguments passed" },
                _,
                "Automation (scripts, AI agents):",
                { "elevate", "Windows only: when ultra does not run as administrator, ask for it with a UAC prompt and run the profiling in an elevated ultra. Its progress, the output of the program and its exit code are relayed to this console, CTRL+C still stops it. A program launched after -- still runs as the current user, not elevated.", v => elevate = v is not null },
                { "result-file=", "Write a JSON summary to {FILE} when the command ends: success, exitCode, error, stopped and the profiles written (path, size). The way for a script or an agent to find the json.gz files.", v => resultFile = v },
                { "stop-file=", "Stop profiling as soon as {FILE} exists (created by another process): the profile is still converted and written, like with CTRL+C. The file is deleted at start if it exists.", v => stopFile = v },
                _,
                "Examples:",
                "  ultra profile --duration 10 -- my_app.exe arg0     Launch my_app.exe and profile its first 10 seconds",
                "  ultra profile --pid 1234,5678                      Attach to running processes until they exit (or 120s)",
                "  ultra profile --elevate --duration 30 --result-file result.json --pid 1234",
                "                                                     From a non-admin shell or an agent (Windows)",
                async (_, arguments) => await ProfileAsync(args, arguments, options, pidList, verbose, elevate, resultFile, stopFile)
            },
            new Command("convert", "Convert an existing trace file (one ETL file, a Linux .ultraperf file with its CLR .nettrace files, or a list of nettrace files) to a Firefox Profiler json file")
            {
                new CommandUsage("Usage: {NAME} --pid xxx [<etl_file_name.etl> | <trace.ultraperf> [<trace_pid_clr.nettrace>] | <file1_sampler.nettrace> <file1_clr.nettrace>]"),
                _,
                new HelpOption(),
                { "o|output=", "The base output {FILE} name. Default is the input file name without the extension.", v => options.BaseOutputFileName = v },
                { "pid=", "The {PID} of the process. Several pids can be separated by commas (or the option repeated): each process gets its own json file.", v => AddPids(pidList, v) },
                { "symbol-path=", $"The {{VALUE}} of symbol path. The default value is `{options.GetCachedSymbolPath()}`.", v => options.SymbolPathText  = v },
                { "nofilter", "ETL only: convert the full ETL file. By default only the CPU samples, stacks and context switches of the --pid process(es) are converted, which is much faster on a busy machine; that filtered .etlx file is kept next to the ETL file and reused.", v => options.FastFilter = v is null },
                { "result-file=", "Write a JSON summary to {FILE} when the command ends (see `ultra profile --help`).", v => resultFile = v },
                { "<>", "The trace file(s) to convert" },
                async (_, arguments) => await ConvertAsync(arguments, options, pidList, verbose, resultFile)
            },
            new Command("report", "Print a text report of a Firefox Profiler json file written by ultra: the CPU per thread, the top functions, the hardware counters per function. Streams the file: fine for a trace of hundreds of MB.")
            {
                new CommandUsage("Usage: {NAME} [Options] <profile.json.gz>"),
                _,
                new HelpOption(),
                { "top=", "How many threads and functions to list. Default is 12.", (int v) => reportTop = v },
                { "offsets", "Also the hot code offsets of the top 3 functions (where in a function's code the samples are).", v => reportOffsets = v is not null },
                { "thread=", "Only the threads whose name contains {TEXT}.", v => reportThread = v },
                { "workers=", "The parsing threads. Default is half the processors.", (int v) => reportWorkers = v },
                { "<>", "The profile json.gz (or json) file" },
                (_, arguments) =>
                {
                    if (arguments.Length != 1)
                    {
                        Terminal.WriteMarkupLine("[red]Expecting one profile file[/]");
                        return ValueTask.FromResult(1);
                    }

                    if (!File.Exists(arguments[0]))
                    {
                        Terminal.WriteMarkupLine($"[red]File not found: {arguments[0]}[/]");
                        return ValueTask.FromResult(1);
                    }

                    var clock = Stopwatch.StartNew();
                    new UltraProfileReport(reportTop, reportOffsets, reportThread, reportWorkers).Run(arguments[0], Console.Out);
                    if (verbose)
                    {
                        Console.Error.WriteLine($"({clock.Elapsed.TotalSeconds:F1} s)");
                    }
                    return ValueTask.FromResult(0);
                }
            },
            new Command("mcp", "Run the ultra MCP server (Model Context Protocol, stdio): AI agents capture profiles and analyze them with its tools")
            {
                new CommandUsage("Usage: {NAME}"),
                _,
                new HelpOption(),
                _,
                "The server talks JSON-RPC on stdin/stdout: configure your MCP client with a stdio server whose command is",
                "`ultra` with the argument `mcp` (e.g. with Claude Code: claude mcp add ultra -- ultra mcp).",
                _,
                "Tools: find_processes, start_profiling, start_conversion, get_job_status, stop_job, list_jobs (capture);",
                "get_profile_report, load_profile, list_threads, call_tree, call_tree_inverted, find_hotspots, focus_function,",
                "module_breakdown, top_functions, list_samples, get_sample, get_call_stack, query_markers, list_counters,",
                "counter_samples, get_llm_guide (analysis). On Windows, start_profiling shows a UAC prompt when the server",
                "is not elevated. See `ultra guide` for the workflow.",
                async (_, _) =>
                {
                    await UltraMcpServer.RunAsync(UltraSelfCommand.ForCurrentProcess(), Version, CancellationToken.None);
                    return 0;
                }
            },
            new Command("guide", "Print the guide for AI agents and automation: capture a profile, read the result, analyze it (report, MCP tools)")
            {
                new CommandUsage("Usage: {NAME}"),
                _,
                new HelpOption(),
                (_, _) =>
                {
                    Console.Out.Write(UltraGuide.Text);
                    return ValueTask.FromResult(0);
                }
            },
            CreateElevatedRunCommand(),
            _,
            "Automation and AI agents:",
            "  ultra guide                       The workflow for agents and scripts: capture, results, analysis, MCP",
            "  ultra profile --elevate --duration 30 --result-file result.json --pid 1234",
            "                                    Capture from a non-admin shell (Windows UAC prompt), JSON summary",
            "  ultra report <profile.json.gz>    Text overview of a profile: CPU per thread, top functions",
            "  ultra mcp                         MCP server (stdio) to capture and analyze profiles from an agent",
        };

        var width = Console.IsOutputRedirected ? 120 : Math.Max(80, Console.WindowWidth);
        var optionWidth = Console.IsOutputRedirected || width == 80 ? 29 : 36;

        try
        {
            return await commandApp.RunAsync(args, new CommandRunConfig(width, optionWidth));
        }
        catch (Exception ex)
        {
            Terminal.WriteMarkupLine($"[red]Unexpected error: {ex.Message}[/]");
            if (verbose)
            {
                Terminal.WriteLine(ex.ToString());
            }
            return 1;
        }
    }

    /// <summary>
    /// The hidden command run by the elevated ultra of `--elevate` (see <see cref="UltraElevation"/>).
    /// </summary>
    private static Command CreateElevatedRunCommand()
    {
        var command = new Command(UltraElevation.ChildCommandName, "Run a command elevated, started by --elevate") { Hidden = true };
        command.Add("<>", "The channel directory");
        command.Action = async (_, arguments) => arguments.Length == 1 ? await UltraElevation.RunChildAsync(arguments[0], RunAsync) : 1;
        return command;
    }

    private static async Task<int> ProfileAsync(string[] allArguments, string[] arguments, UltraProfilerOptions options, List<int> pidList, bool verbose, bool elevate, string? resultFile, string? stopFile)
    {
        // `--pmc list` (or help): the accepted counters, then exit
        if (options.HardwareCounters.Any(x => x.Equals("list", StringComparison.OrdinalIgnoreCase) || x.Equals("help", StringComparison.OrdinalIgnoreCase)))
        {
            Terminal.WriteLine(PerfEventSpec.ListText);
            return 0;
        }

        var child = UltraElevation.Child;
        var output = child?.Output ?? UltraOutput.Create(options.ConsoleMode, verbose, interactive: !options.Paused);
        var result = new UltraCommandResult { Command = "profile", StartTime = DateTimeOffset.Now };
        var clock = Stopwatch.StartNew();
        bool writeResult = resultFile is not null;
        try
        {
            if (arguments.Length == 0 && pidList.Count == 0)
            {
                return Fail(result, output, "Missing pid or executable name");
            }

            if (options.HardwareCounters.Count > 0 && !OperatingSystem.IsLinux())
            {
                return Fail(result, output, "--pmc (hardware counters) is only supported on Linux");
            }

            if (!UltraProfiler.IsElevated())
            {
                if (elevate && OperatingSystem.IsWindows() && child is null)
                {
                    if (options.Paused)
                    {
                        return Fail(result, output, "--paused cannot be used with --elevate: use --delay instead");
                    }

                    // The elevated ultra runs the command and writes the result file
                    writeResult = false;
                    try
                    {
                        return await UltraElevation.RunElevatedAsync(allArguments, output, options.ConsoleMode);
                    }
                    catch (UltraElevationException ex)
                    {
                        writeResult = resultFile is not null;
                        return Fail(result, output, ex.Message);
                    }
                }

                return Fail(result, output, OperatingSystem.IsWindows()
                    ? $"{UltraProfiler.ElevationRequirement}: run from an elevated prompt, or add --elevate to ask for it with a UAC prompt"
                    : UltraProfiler.ElevationRequirement);
            }

            // Add the pid passed as options
            options.ProcessIds.AddRange(pidList);

            if (arguments.Length == 1 && arguments[0].Length > 0 && arguments[0].All(c => char.IsAsciiDigit(c) || c == ','))
            {
                var pids = new List<int>();
                AddPids(pids, arguments[0]);
                options.ProcessIds.AddRange(pids.Except(options.ProcessIds).ToList());
            }
            else if (arguments.Length > 0)
            {
                options.ProgramPath = arguments[0];
                options.Arguments.AddRange(arguments.AsSpan().Slice(1));
            }

            if (child is not null)
            {
                // The program runs as the user, started by the ultra that is not elevated
                options.ProgramStarter = child.StartProgramAsync;
            }

            output.Info(stopFile is null ? "You can press CTRL+C to stop profiling before the end of the process" : $"Profiling stops at the end of the process, after {options.DurationInSeconds}s, or when {stopFile} exists");

            options.EnsureDirectoryForBaseOutputFileName();

            var profiler = UltraProfiler.Create();

            ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                result.Stopped = true;
                output.Warning("Cancelled via CTRL+C");
                // ReSharper disable once AccessToDisposedClosure
                if (profiler.Cancel())
                {
                    output.Error("Stopped via CTRL+C");
                }
            };
            Console.CancelKeyPress += onCancel;

            using var stopMonitor = UltraStopMonitor.Start(stopFile, child,
                reason =>
                {
                    result.Stopped = true;
                    if (reason.Length > 0)
                    {
                        output.Warning(reason);
                    }

                    profiler.Cancel();
                },
                () =>
                {
                    output.Error("Profiling aborted");
                    // A second cancel waits for the profiler to stop: not on the timer thread
                    ThreadPool.QueueUserWorkItem(_ => profiler.Cancel());
                });

            // Handle paused
            if (options.Paused)
            {
                options.ShouldStartProfiling = () => ShouldStartProfiling(output, profiler);
            }

            output.Attach(options);

            List<string> jsonGzOutputs = [];
            try
            {
                await output.RunAsync("Profiling", async () =>
                {
                    try
                    {
                        jsonGzOutputs = await profiler.Run(options);
                    }
                    finally
                    {
                        profiler.Dispose();
                    }

                    if (verbose)
                    {
                        options.LogProgress?.Invoke("Profiling Done");
                    }
                });
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }

            if (jsonGzOutputs.Count == 0)
            {
                return Fail(result, output, "The profiler did not generate any trace file");
            }

            PrintGeneratedFiles(output, jsonGzOutputs, result);
            result.Success = true;
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail(result, output, ex is InvalidOperationException { Message: "CTRL+C requested" } ? "Profiling aborted" : ex.Message, ex, verbose);
        }
        finally
        {
            if (writeResult)
            {
                WriteResult(result, resultFile!, clock);
            }
        }
    }

    private static bool ShouldStartProfiling(UltraOutput output, UltraProfiler profiler)
    {
        Console.TreatControlCAsInput = true;
        output.Info("Press SPACE or ENTER to start profiling");
        var key = Console.ReadKey(true);
        bool startProfiling = key.Key == ConsoleKey.Spacebar || key.Key == ConsoleKey.Enter;

        bool isCtrlC = key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.C;
        if (startProfiling || isCtrlC)
        {
            // Restore the default behavior so that CancelKeyPress will be called later if CTRL+C is pressed
            Console.TreatControlCAsInput = false;
        }

        if (isCtrlC)
        {
            output.Warning("Cancelled via CTRL+C");
            profiler.Cancel();
        }
        else if (!startProfiling)
        {
            output.Warning($"Key pressed {key.Modifiers} {key.Key}");
        }

        return startProfiling;
    }

    private static async Task<int> ConvertAsync(string[] arguments, UltraProfilerOptions options, List<int> pidList, bool verbose, string? resultFile)
    {
        var output = UltraElevation.Child?.Output ?? UltraOutput.Create(UltraProfilerConsoleMode.Silent, verbose);
        var result = new UltraCommandResult { Command = "convert", StartTime = DateTimeOffset.Now };
        var clock = Stopwatch.StartNew();
        try
        {
            if (arguments.Length == 0)
            {
                return Fail(result, output, "Missing trace file name");
            }

            if (pidList.Count == 0)
            {
                return Fail(result, output, "Missing --pid option");
            }

            output.Attach(options);
            options.EnsureDirectoryForBaseOutputFileName();

            List<string> fileOutputs = [];
            var profiler = UltraProfiler.Create();
            ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
            {
                output.Warning("Cancelled via CTRL+C");
                eventArgs.Cancel = true;
                // ReSharper disable once AccessToDisposedClosure
                if (profiler.Cancel())
                {
                    output.Error("Stopped via CTRL+C");
                }
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                await output.RunAsync("Converting", async () =>
                {
                    try
                    {
                        var traceFiles = arguments.Select(x => new UltraProfilerTraceFile(x)).ToList();
                        fileOutputs = await profiler.Convert(GetConvertBaseName(traceFiles[0]), traceFiles, pidList, options);
                    }
                    finally
                    {
                        profiler.Dispose();
                    }

                    if (verbose)
                    {
                        options.LogProgress?.Invoke("Converting Done");
                    }
                });
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }

            PrintGeneratedFiles(output, fileOutputs, result);
            result.Success = fileOutputs.Count > 0;
            return result.Success ? 0 : Fail(result, output, "The conversion did not generate any file");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail(result, output, ex.Message, ex, verbose);
        }
        finally
        {
            if (resultFile is not null)
            {
                WriteResult(result, resultFile, clock);
            }
        }
    }

    /// <summary>
    /// Recovers the base name of the profile from the first trace file.
    /// </summary>
    private static string GetConvertBaseName(UltraProfilerTraceFile traceFile)
    {
        string baseName = Path.GetFileNameWithoutExtension(traceFile.FileName);
        // The unmerged ETL files of a profile (x.kernel.etl, x.user.etl, x.rundown.etl)
        foreach (var postfix in (ReadOnlySpan<string>)[".kernel", ".user", ".rundown"])
        {
            if (baseName.EndsWith(postfix, StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName.Substring(0, baseName.Length - postfix.Length);
                break;
            }
        }

        if (traceFile.IsNettrace)
        {
            if (baseName.EndsWith(UltraProfiler.NettracePostfixNameSampler))
            {
                baseName = baseName.Substring(0, baseName.Length - UltraProfiler.NettracePostfixNameSampler.Length);
            }
            else if (baseName.EndsWith(UltraProfiler.NettracePostfixNameClr))
            {
                baseName = baseName.Substring(0, baseName.Length - UltraProfiler.NettracePostfixNameClr.Length);
            }
        }

        return baseName;
    }

    private static int Fail(UltraCommandResult result, UltraOutput output, string message, Exception? exception = null, bool verbose = false)
    {
        result.Success = false;
        result.Error = message;
        output.Error(exception is null ? message : $"Unexpected error: {message}");
        if (exception is not null && verbose)
        {
            output.Text(exception.ToString());
        }

        return 1;
    }

    private static void WriteResult(UltraCommandResult result, string resultFile, Stopwatch clock)
    {
        result.ExitCode = result.Success ? 0 : 1;
        result.Elevated = UltraProfiler.IsElevated();
        result.DurationSeconds = Math.Round(clock.Elapsed.TotalSeconds, 3);
        result.WriteTo(resultFile);
    }

    /// <summary>
    /// Adds the pids of a --pid option value: one pid or several separated by commas.
    /// </summary>
    private static void AddPids(List<int> pidList, string? value)
    {
        foreach (var item in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(item, out var pid))
            {
                throw new ArgumentException($"Invalid pid `{item}` in --pid {value}");
            }

            if (!pidList.Contains(pid))
            {
                pidList.Add(pid);
            }
        }
    }

    private static void PrintGeneratedFiles(UltraOutput output, List<string> files, UltraCommandResult result)
    {
        foreach (var file in files)
        {
            var fullPath = Path.GetFullPath(file);
            var size = new FileInfo(fullPath).Length;
            result.Profiles.Add(new UltraCommandResultFile { Path = fullPath, Size = size });
            output.MarkupLine($"Generated Firefox Profiler JSON file -> [green]{UltraOutput.Escape(file)}[/] - {ByteSize.FromBytes(size)}");
        }

        if (files.Count > 0)
        {
            output.MarkupLine("Go to [blue]https://profiler.firefox.com/[/] to open it, or analyze it with `ultra report` or `ultra mcp`");
        }
    }
}
