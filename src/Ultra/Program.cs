using System;
using System.Diagnostics.Tracing;
using System.Text;
using ByteSizeLib;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Ultra.Core;
using XenoAtom.CommandLine;

namespace Ultra;

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
                { "pid=", "The {PID} of the process", (int pid) => { pidList.Add(pid); } },
                { "keep-merged-etl-file", "Keep the merged ETL file.", v => options.KeepMergedEtl = v is not null },
                { "keep-intermediate-etl-files", "Keep the intermediate ETL files before merging.", v => options.KeepEtlIntermediateFiles = v is not null },
                { "sampling-interval=", $"The {{VALUE}} of the sample interval in ms. Default is 8190Hz = {options.CpuSamplingIntervalInMs:0.000}ms.", (float v) => options.CpuSamplingIntervalInMs  = v },
                { "symbol-path=", $"The {{VALUE}} of symbol path. The default value is `{options.GetCachedSymbolPath()}`.", v => options.SymbolPathText  = v },
                // Action for the commit commandd
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

                    await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Default)
                        .SpinnerStyle(Style.Parse("red"))
                        .StartAsync("Profiling", async statusCtx =>
                            {
                                string? previousText = null;
                                
                                options.LogStepProgress = (text) =>
                                {
                                    if (verbose && previousText != text)
                                    {
                                        AnsiConsole.MarkupLine($"{previousText} [green]\u2713[/]");
                                        previousText = text;
                                    }

                                    statusCtx.Status($"{text}");
                                };
                                options.LogProgress = (text) =>
                                {
                                    if (verbose && previousText != null && previousText != text)
                                    {
                                        AnsiConsole.MarkupLine($"{previousText} [green]\u2713[/]");
                                    }

                                    statusCtx.Status(text);
                                    previousText = text;
                                };
                                options.WaitingFileToComplete = (file) => { statusCtx.Status($"Waiting for {file} to complete"); };
                                options.WaitingFileToCompleteTimeOut = (file) => { statusCtx.Status($"Timeout waiting for {file} to complete"); };

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
                                
                                var etwProfiler = new EtwUltraProfiler();
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
                                    if (verbose && previousText != text)
                                    {
                                        AnsiConsole.MarkupLine($"{previousText} [green]\u2713[/]");
                                        previousText = text;
                                    }

                                    statusCtx.Status($"{text}");
                                };

                                options.LogProgress = (text) =>
                                {
                                    if (verbose && previousText != null && previousText != text)
                                    {
                                        AnsiConsole.MarkupLine($"{previousText} [green]\u2713[/]");
                                    }

                                    statusCtx.Status(text);
                                    previousText = text;
                                };

                                var etwProfiler = new EtwUltraProfiler();
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
}