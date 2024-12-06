// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.
using System.Text;
using ByteSizeLib;
using Spectre.Console;
using Ultra.Core;
using XenoAtom.CommandLine;

namespace Ultra;

/// <summary>
/// Main entry point for the ultra command line.
/// </summary>
internal class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        List<int> pidList = new();

        bool verbose = false;
        var options = new EtwUltraProfilerOptions();

        const string _ = "";

        var commandApp = new CommandApp("ultra", "Profile an application")
        {
            new CommandUsage("Usage: {NAME} [Options] command "),
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
                { "pid=", "The {PID} of the process to attach the profiler to.", (int pid) => { pidList.Add(pid); } },
                { "sampling-interval=", $"The {{VALUE}} of the sample interval in ms. Default is 8190Hz = {options.CpuSamplingIntervalInMs:0.000}ms.", (float v) => options.CpuSamplingIntervalInMs  = v },
                { "symbol-path=", $"The {{VALUE}} of symbol path. The default value is `{options.GetCachedSymbolPath()}`.", v => options.SymbolPathText  = v },
                { "paused", "Launch the profiler paused and wait for SPACE or ENTER keys to be pressed.", v => options.Paused = v is not null },
                { "delay=", $"Starts profiling after a specific delay (seconds). Default is {options.DelayInSeconds}s.", (double delay) => options.DelayInSeconds = delay },
                { "duration=", $"Run the profiler for a maximum duration (seconds). Default is {options.DurationInSeconds}s", (double duration) => options.DurationInSeconds = duration },
                { "keep-merged-etl-file", "Keep the merged ETL file.", v => options.KeepMergedEtl = v is not null },
                { "keep-intermediate-etl-files", "Keep the intermediate ETL files before merging.", v => options.KeepEtlIntermediateFiles = v is not null },
                { "mode=", "Defines how the stdout/stderr of a program explicitly started by ultra should be integrated in its output. Default is `silent` which will not mix program's output. The other options are: `raw` is going to mix ultra and program output together in a raw output. `live` is going to mix ultra and program output within a live table.", v =>
                    {
                        if ("raw".Equals(v, StringComparison.OrdinalIgnoreCase))
                        {
                            options.ConsoleMode = EtwUltraProfilerConsoleMode.Raw;
                        }
                        else if ("live".Equals(v, StringComparison.OrdinalIgnoreCase))
                        {
                            options.ConsoleMode = EtwUltraProfilerConsoleMode.Live;
                        }
                        else
                        {
                            options.ConsoleMode = EtwUltraProfilerConsoleMode.Silent;
                        }
                    }
                },
                // Action for the commit command
                async (ctx, arguments) =>
                {
                    if (arguments.Length == 0 && pidList.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[red]Missing pid or executable name[/]");
                        return 1;
                    }

                    if (!EtwUltraProfiler.IsElevated())
                    {
                        AnsiConsole.MarkupLine("[darkorange]This command requires to run with administrator rights[/]");
                        return 1;
                    }

                    string? fileOutput = null;

                    
                    // Add the pid passed as options
                    options.ProcessIds.AddRange(pidList);

                    if (arguments.Length == 1 && int.TryParse(arguments[0], out var pid))
                    {
                        options.ProcessIds.Add(pid);
                    }
                    else if (arguments.Length > 0)
                    {
                        options.ProgramPath = arguments[0];
                        options.Arguments.AddRange(arguments.AsSpan().Slice(1));
                    }

                    AnsiConsole.MarkupLine($"[green]You can press CTRL+C to stop profiling before the end of the process[/]");

                    options.EnsureDirectoryForBaseOutputFileName();

                    var etwProfiler = EtwUltraProfiler.Create();

                    Console.CancelKeyPress += (sender, eventArgs) =>
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[darkorange]Cancelled via CTRL+C[/]");
                        eventArgs.Cancel = true;
                        // ReSharper disable once AccessToDisposedClosure
                        if (etwProfiler.Cancel())
                        {
                            AnsiConsole.MarkupLine("[red]Stopped via CTRL+C[/]");
                        }
                    };

                    // Handle paused
                    if (options.Paused)
                    {
                        Console.TreatControlCAsInput = true;

                        options.ShouldStartProfiling = () =>
                        {
                            AnsiConsole.MarkupLine("[green]Press SPACE or ENTER to start profiling[/]");
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
                                AnsiConsole.MarkupLine("[darkorange]Cancelled via CTRL+C[/]");
                                etwProfiler.Cancel();
                            }
                            else if (!startProfiling)
                            {
                                AnsiConsole.MarkupLine($"[darkorange]Key pressed {key.Modifiers} {key.Key}[/]");
                            }
                            
                            return startProfiling;
                        };
                    }

                    if (options.ConsoleMode == EtwUltraProfilerConsoleMode.Silent)
                    {
                        await AnsiConsole.Status()
                            .Spinner(Spinner.Known.Default)
                            .SpinnerStyle(Style.Parse("red"))
                            .StartAsync("Profiling", async statusCtx =>
                                {
                                    string? previousText = null;

                                    options.LogStepProgress = (text) =>
                                    {
                                        if (verbose && previousText is not null && previousText != text)
                                        {
                                            AnsiConsole.MarkupLine($"{Markup.Escape(previousText)} [green]\u2713[/]");
                                            previousText = text;
                                        }

                                        statusCtx.Status(Markup.Escape(text));
                                    };
                                    options.LogProgress = (text) =>
                                    {
                                        if (verbose && previousText != null && previousText != text)
                                        {
                                            AnsiConsole.MarkupLine($"{Markup.Escape(previousText)} [green]\u2713[/]");
                                        }

                                        statusCtx.Status(Markup.Escape(text));
                                        previousText = text;
                                    };
                                    options.WaitingFileToComplete = (file) => { statusCtx.Status($"Waiting for {Markup.Escape(file)} to complete"); };
                                    options.WaitingFileToCompleteTimeOut = (file) => { statusCtx.Status($"Timeout waiting for {Markup.Escape(file)} to complete"); };

                                    try
                                    {
                                        fileOutput = await etwProfiler.Run(options);
                                    }
                                    finally
                                    {
                                        etwProfiler.Dispose();
                                    }

                                    if (verbose)
                                    {
                                        options.LogProgress.Invoke("Profiling Done");
                                    }
                                }
                            );
                    }
                    else if (options.ConsoleMode == EtwUltraProfilerConsoleMode.Raw)
                    {
                        options.LogStepProgress = s => AnsiConsole.WriteLine($">>ultra::{s}");
                        options.LogProgress = s => AnsiConsole.WriteLine($">>ultra::{s}");
                        options.WaitingFileToComplete = (file) => { AnsiConsole.WriteLine($">>ultra::Waiting for {Markup.Escape(file)} to complete"); };
                        options.WaitingFileToCompleteTimeOut = (file) => { AnsiConsole.WriteLine($">>ultra::Timeout waiting for {Markup.Escape(file)} to complete"); };
                        options.ProgramLogStdout = AnsiConsole.WriteLine;
                        options.ProgramLogStderr = AnsiConsole.WriteLine;
                        
                        try
                        {
                            fileOutput = await etwProfiler.Run(options);
                        }
                        finally
                        {
                            etwProfiler.Dispose();
                        }
                    }
                    else if (options.ConsoleMode == EtwUltraProfilerConsoleMode.Live)
                    {
                        var statusTable = new StatusTable();

                        await AnsiConsole.Live(statusTable.Table)
                            // .AutoClear(true) // No auto clear to keep the output (e.g. in case the program shows errors in its stdout/stderr)
                            .StartAsync(async liveCtx =>
                            {
                                string? previousText = null;

                                options.LogStepProgress = (text) =>
                                {
                                    if (verbose && previousText is not null && previousText != text)
                                    {
                                        statusTable.LogText($"{Markup.Escape(previousText)} [green]\u2713[/]");
                                        previousText = text;
                                    }

                                    statusTable.Status(Markup.Escape(text));

                                    statusTable.UpdateTable();
                                    liveCtx.Refresh();
                                };

                                options.LogProgress = (text) =>
                                {
                                    if (verbose && previousText != null && previousText != text)
                                    {
                                        statusTable.LogText($"{Markup.Escape(previousText)} [green]\u2713[/]");
                                    }

                                    statusTable.Status(Markup.Escape(text));
                                    previousText = text;

                                    statusTable.UpdateTable();
                                    liveCtx.Refresh();
                                };

                                options.WaitingFileToComplete = (file) =>
                                {
                                    statusTable.Status($"Waiting for {Markup.Escape(file)} to complete");
                                    statusTable.UpdateTable();
                                    liveCtx.Refresh();
                                };

                                options.WaitingFileToCompleteTimeOut = (file) =>
                                {
                                    statusTable.Status($"Timeout waiting for {Markup.Escape(file)} to complete");
                                    statusTable.UpdateTable();
                                    liveCtx.Refresh();
                                };

                                options.ProgramLogStdout = (text) =>
                                {
                                    statusTable.LogText(Markup.Escape(text));
                                    statusTable.UpdateTable();
                                    liveCtx.Refresh();
                                };

                                options.ProgramLogStderr = (text) =>
                                {
                                    statusTable.LogText(Markup.Escape(text));
                                    statusTable.UpdateTable();
                                    liveCtx.Refresh();
                                };
                                
                                try
                                {
                                    fileOutput = await etwProfiler.Run(options);
                                }
                                finally
                                {
                                    etwProfiler.Dispose();
                                }
                            }
                        );
                    }

                    if (fileOutput != null)
                    {
                        AnsiConsole.MarkupLine($"Generated Firefox Profiler JSON file -> [green]{fileOutput}[/] - {ByteSize.FromBytes(new FileInfo(fileOutput).Length)}");
                        AnsiConsole.MarkupLine($"Go to [blue]https://profiler.firefox.com/ [/]");
                    }

                    return 0;
                }
            },
            new Command("convert", "Convert an existing ETL file to a Firefox Profiler json file")
            {
                new CommandUsage("Usage: {NAME} --pid xxx <etl_file_name.etl>"),
                _,
                new HelpOption(),
                { "o|output=", "The base output {FILE} name. Default is the input file name without the extension.", v => options.BaseOutputFileName = v },
                { "pid=", "The {PID} of the process", (int pid) => { pidList.Add(pid); } },
                { "symbol-path=", $"The {{VALUE}} of symbol path. The default value is `{options.GetCachedSymbolPath()}`.", v => options.SymbolPathText  = v },
                async (ctx, arguments) =>
                {
                    var maxWidth = Console.IsOutputRedirected ? 80 : Console.WindowWidth;

                    if (arguments.Length == 0)
                    {
                        AnsiConsole.MarkupLine("[red]Missing ETL file name[/]");
                        return 1;
                    }

                    if (pidList.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[red]Missing --pid option[/]");
                        return 1;
                    }

                    var etlFile = arguments[0];

                    string? fileOutput = null;

                    await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Default)
                        .SpinnerStyle(Style.Parse("red"))
                        .StartAsync("Converting", async statusCtx =>
                            {
                                string? previousText = null;

                                options.LogStepProgress = (text) =>
                                {
                                    if (verbose && previousText is not null && previousText != text)
                                    {
                                        AnsiConsole.MarkupLine($"{Markup.Escape(previousText)} [green]\u2713[/]");
                                        previousText = text;
                                    }

                                    statusCtx.Status(Markup.Escape(text));
                                };

                                options.LogProgress = (text) =>
                                {
                                    if (verbose && previousText != null && previousText != text)
                                    {
                                        AnsiConsole.MarkupLine($"{Markup.Escape(previousText)} [green]\u2713[/]");
                                    }

                                    statusCtx.Status(Markup.Escape(text));
                                    previousText = text;
                                };

                                var etwProfiler = EtwUltraProfiler.Create();
                                try
                                {
                                    Console.CancelKeyPress += (sender, eventArgs) =>
                                    {
                                        AnsiConsole.WriteLine();
                                        AnsiConsole.MarkupLine("[darkorange]Cancelled via CTRL+C[/]");

                                        eventArgs.Cancel = true;
                                        if (etwProfiler.Cancel())
                                        {
                                            AnsiConsole.MarkupLine("[red]Stopped via CTRL+C[/]");
                                        }
                                    };

                                    options.EnsureDirectoryForBaseOutputFileName();

                                    fileOutput = await etwProfiler.Convert(etlFile, pidList, options);
                                }
                                finally
                                {
                                    etwProfiler.Dispose();
                                }

                                if (verbose)
                                {
                                    options.LogProgress.Invoke("Converting Done");
                                }
                            }
                        );


                    if (fileOutput != null)
                    {
                        AnsiConsole.MarkupLine($"Generated Firefox Profiler JSON file -> [green]{fileOutput}[/] - {ByteSize.FromBytes(new FileInfo(fileOutput).Length)}");
                        AnsiConsole.MarkupLine($"Go to [blue]https://profiler.firefox.com/ [/]");
                    }

                    return 0;
                }
            }
        };

        var width = Console.IsOutputRedirected ? 80 : Math.Max(80, Console.WindowWidth);
        var optionWidth = Console.IsOutputRedirected || width == 80 ? 29 : 36;
        
        try
        {
            return await commandApp.RunAsync(args, new CommandRunConfig(width, optionWidth));
        }
        catch (Exception ex)
        {
            AnsiConsole.Foreground = Color.Red;
            AnsiConsole.WriteLine($"Unexpected error: {ex.Message}");
            AnsiConsole.ResetColors();
            if (verbose)
            {
                AnsiConsole.WriteLine(ex.ToString());
            }
            return 1;
        }
    }
    
    private class StatusTable
    {
        private string? _statusText;
        private readonly Queue<string> _logLines = new();
        private const int MaxLogLines = 10;
        private int _spinnerStep;
        private readonly Style _spinnerStyle;

        public StatusTable()
        {
            Table = new Table();
            Table.AddColumn(new TableColumn("Status"));
            _spinnerStyle = Style.Parse("red");
        }

        public Table Table { get; }
        
        public void LogText(string text)
        {
            if (_logLines.Count > MaxLogLines)
            {
                _logLines.Dequeue();
            }

            _logLines.Enqueue(text);
        }
        
        public void Status(string text)
        {
            _statusText = text;
        }

        public void UpdateTable()
        {
            Table.Rows.Clear();

            if (_logLines.Count > 0)
            {
                var rows = new Rows(_logLines.Select(x => new Markup(x)));
                Table.AddRow(rows);
            }

            if (_statusText != null)
            {
                var tableColumn = Table.Columns[0];

                var spinnerStep = _spinnerStep;
                var spinnerText = Spinner.Known.Default.Frames[spinnerStep];
                _spinnerStep = (_spinnerStep + 1) % Spinner.Known.Default.Frames.Count;

                tableColumn.Header = new Markup($"[red]{spinnerText}[/] Status");
                tableColumn.Footer = new Markup(_statusText);
                Table.ShowFooters = true;
            }
        }
    }
}
