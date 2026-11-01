// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;
using Ultra.Core;

namespace Ultra;

/// <summary>
/// The console output of a command: permanent lines, a transient status (progress) and the output of a launched program.
/// </summary>
/// <remarks>
/// Three implementations: <see cref="UltraLiveOutput"/> for an interactive terminal (inline live region with a spinner),
/// <see cref="UltraPlainOutput"/> when the output is redirected (scripts, CI, AI agents) or in `--mode raw`,
/// and <see cref="UltraChannelOutput"/> in the elevated ultra, which sends everything to the ultra that started it.
/// All the members are thread-safe: the profiler reports its progress from other threads.
/// </remarks>
internal abstract class UltraOutput
{
    /// <summary>
    /// Writes a permanent line of markup (see <see cref="Escape"/> for the text inserted in it).
    /// </summary>
    public abstract void MarkupLine(string markup);

    /// <summary>
    /// Updates the transient status (e.g. "Profiling Process X - 10s - 12 MB").
    /// </summary>
    public abstract void Status(string text);

    /// <summary>
    /// Reports a step of the command (e.g. "Merging ETL Files").
    /// </summary>
    public abstract void Step(string text);

    /// <summary>
    /// Writes a line of the output of the profiled program (`--mode raw` or `--mode live`).
    /// </summary>
    public abstract void ProgramOutput(string line, bool isError);

    /// <summary>
    /// Runs the work of a command while showing its progress.
    /// </summary>
    public virtual Task RunAsync(string initialStatus, Func<Task> work)
    {
        Status(initialStatus);
        return work();
    }

    /// <summary>
    /// Escapes a text inserted in markup.
    /// </summary>
    public static string Escape(string text) => AnsiMarkup.Escape(text);

    public void Error(string text) => MarkupLine($"[red]{Escape(text)}[/]");

    public void Warning(string text) => MarkupLine($"[darkorange]{Escape(text)}[/]");

    public void Info(string text) => MarkupLine($"[green]{Escape(text)}[/]");

    public void Text(string text) => MarkupLine(Escape(text));

    /// <summary>
    /// Connects the progress callbacks of the profiler options to this output.
    /// </summary>
    public void Attach(UltraProfilerOptions options)
    {
        options.LogStepProgress = Status;
        options.LogProgress = Step;
        options.LogWarning = Warning;
        options.WaitingFileToComplete = file => Status($"Waiting for {file} to complete");
        options.WaitingFileToCompleteTimeOut = file => Status($"Timeout waiting for {file} to complete");
        options.ProgramLogStdout = line => ProgramOutput(line, false);
        options.ProgramLogStderr = line => ProgramOutput(line, true);
    }

    /// <summary>
    /// Creates the output of a command run from a terminal.
    /// </summary>
    /// <param name="mode">The console mode of the profiled program.</param>
    /// <param name="verbose">Prints the completed steps.</param>
    /// <param name="interactive">False to never use a live region (e.g. `--paused`, which reads keys from the console).</param>
    public static UltraOutput Create(UltraProfilerConsoleMode mode, bool verbose, bool interactive = true)
    {
        if (mode == UltraProfilerConsoleMode.Raw || !interactive || !IsInteractiveTerminal)
        {
            return new UltraPlainOutput(mode == UltraProfilerConsoleMode.Raw ? ">>ultra::" : null, verbose);
        }

        return new UltraLiveOutput(mode == UltraProfilerConsoleMode.Live, verbose);
    }

    /// <summary>
    /// True when the output is a terminal that supports a live region (not redirected, ANSI enabled).
    /// </summary>
    public static bool IsInteractiveTerminal => !Console.IsOutputRedirected && Terminal.Capabilities.AnsiEnabled && !Terminal.Capabilities.IsOutputRedirected;
}

/// <summary>
/// Plain lines, for redirected output (scripts, CI, AI agents) and `--mode raw`: the steps are printed as they come,
/// the status at most every few seconds.
/// </summary>
internal sealed class UltraPlainOutput(string? prefix, bool verbose) : UltraOutput
{
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(5);
    private readonly Lock _lock = new();
    private readonly Stopwatch _statusClock = new();
    private string? _lastStep;

    public override void MarkupLine(string markup)
    {
        lock (_lock)
        {
            Terminal.WriteMarkupLine(markup);
        }
    }

    public override void Status(string text)
    {
        lock (_lock)
        {
            if (_statusClock.IsRunning && _statusClock.Elapsed < StatusInterval)
            {
                return;
            }

            _statusClock.Restart();
            WriteLine(text);
        }
    }

    public override void Step(string text)
    {
        lock (_lock)
        {
            // Without --verbose, a step repeated with a new percentage (e.g. "Loading perf events 10%") is printed once
            var stepKey = text.Contains('%') ? WithoutDigits(text) : null;
            if (!verbose && stepKey is not null && stepKey == _lastStep)
            {
                return;
            }

            _lastStep = stepKey;
            _statusClock.Restart();
            WriteLine(text);
        }
    }

    public override void ProgramOutput(string line, bool isError)
    {
        lock (_lock)
        {
            Terminal.WriteLine(line);
        }
    }

    private void WriteLine(string text) => Terminal.WriteLine(prefix is null ? text : $"{prefix}{text}");

    private static string WithoutDigits(string text) => string.Concat(text.Where(c => !char.IsAsciiDigit(c)));
}

/// <summary>
/// An inline live region on an interactive terminal: a spinner with the status, and with `--mode live` the last lines of
/// the profiled program. The permanent lines are written above the region.
/// </summary>
internal sealed class UltraLiveOutput(bool showProgramOutput, bool verbose) : UltraOutput
{
    private const int MaxProgramLines = 10;
    private readonly ConcurrentQueue<string> _pendingLines = new();
    private readonly Queue<string> _programLines = new();
    private readonly Lock _lock = new();
    private volatile string _status = string.Empty;
    private string? _previousStep;
    private volatile bool _running;

    public override void MarkupLine(string markup)
    {
        if (_running)
        {
            _pendingLines.Enqueue(markup);
        }
        else
        {
            Terminal.WriteMarkupLine(markup);
        }
    }

    public override void Status(string text) => _status = text;

    public override void Step(string text)
    {
        lock (_lock)
        {
            if (verbose && _previousStep is not null && _previousStep != text)
            {
                MarkupLine($"{Escape(_previousStep)} [green]✓[/]");
            }

            _previousStep = text;
        }

        _status = text;
    }

    public override void ProgramOutput(string line, bool isError)
    {
        lock (_lock)
        {
            _programLines.Enqueue(line);
            if (_programLines.Count > MaxProgramLines)
            {
                _programLines.Dequeue();
            }
        }
    }

    public override async Task RunAsync(string initialStatus, Func<Task> work)
    {
        _status = initialStatus;
        var status = new State<string>(initialStatus);
        var programText = new State<string>(string.Empty);

        var statusLine = new HStack(
                new Spinner().Style(SpinnerStyles.Dots),
                new TextBlock(() => status.Value))
            .Spacing(1);

        Visual root = showProgramOutput
            ? new VStack(new Group(new Markup("[bold]Program output[/]"), new TextBlock(() => programText.Value)), statusLine)
            : statusLine;

        // Keep Ctrl+C a signal (Console.CancelKeyPress stops the profiling) instead of a key of the live region
        if (!Terminal.IsInputRunning)
        {
            Terminal.StartInput(new TerminalInputOptions { TreatControlCAsInput = false, CaptureCtrlC = false, CaptureCtrlBreak = false });
        }

        _running = true;
        var task = Task.Run(work);
        try
        {
            await Terminal.LiveAsync(root, async _ =>
            {
                await Task.WhenAny(task, Task.Delay(100));
                FlushPendingLines();
                status.Value = _status;
                if (showProgramOutput)
                {
                    lock (_lock)
                    {
                        programText.Value = string.Join("\n", _programLines);
                    }
                }

                // Remove the live region at the end: the final messages are written after it
                return task.IsCompleted ? TerminalLoopResult.Stop : TerminalLoopResult.Continue;
            });
        }
        finally
        {
            _running = false;
            FlushPendingLines();
        }

        if (showProgramOutput)
        {
            // Keep the last lines of the program visible after the region is removed
            lock (_lock)
            {
                foreach (var line in _programLines)
                {
                    Terminal.WriteLine(line);
                }
            }
        }

        if (verbose && _previousStep is not null)
        {
            Terminal.WriteMarkupLine($"{_previousStep} [green]✓[/]");
        }

        await task;
    }

    private void FlushPendingLines()
    {
        while (_pendingLines.TryDequeue(out var line))
        {
            Terminal.WriteMarkupLine(line);
        }
    }
}

/// <summary>
/// The output of the elevated ultra (`--elevate`): every call is appended as a JSON line to a file that the ultra which
/// started it reads and replays on its own console (see <see cref="UltraElevation"/>).
/// </summary>
internal sealed class UltraChannelOutput : UltraOutput, IDisposable
{
    private readonly Lock _lock = new();
    private readonly FileStream _stream;

    public UltraChannelOutput(string path)
    {
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    public override void MarkupLine(string markup) => Write(UltraChannelMessage.MarkupLine, markup);

    public override void Status(string text) => Write(UltraChannelMessage.Status, text);

    public override void Step(string text) => Write(UltraChannelMessage.Step, text);

    public override void ProgramOutput(string line, bool isError) => Write(isError ? UltraChannelMessage.ProgramError : UltraChannelMessage.ProgramOutput, line);

    private void Write(string kind, string text)
    {
        var buffer = new ArrayBufferWriterStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("k", kind);
            writer.WriteString("t", text);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        lock (_lock)
        {
            _stream.Write(buffer.WrittenSpan);
            _stream.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stream.Dispose();
        }
    }

    private sealed class ArrayBufferWriterStream : MemoryStream
    {
        public ReadOnlySpan<byte> WrittenSpan => GetBuffer().AsSpan(0, (int)Length);
    }
}

/// <summary>
/// The kinds of the JSON lines of <see cref="UltraChannelOutput"/>.
/// </summary>
internal static class UltraChannelMessage
{
    public const string MarkupLine = "markup";
    public const string Status = "status";
    public const string Step = "step";
    public const string ProgramOutput = "out";
    public const string ProgramError = "err";

    /// <summary>
    /// Replays a JSON line written by <see cref="UltraChannelOutput"/> on <paramref name="output"/>.
    /// </summary>
    /// <returns>False when the line is not a valid message.</returns>
    public static bool Replay(string line, UltraOutput output)
    {
        string? kind = null, text = null;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString();
                    reader.Read();
                    if (name == "k") kind = reader.GetString();
                    else if (name == "t") text = reader.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        if (kind is null || text is null)
        {
            return false;
        }

        switch (kind)
        {
            case MarkupLine: output.MarkupLine(text); break;
            case Status: output.Status(text); break;
            case Step: output.Step(text); break;
            case ProgramOutput: output.ProgramOutput(text, false); break;
            case ProgramError: output.ProgramOutput(text, true); break;
            default: return false;
        }

        return true;
    }
}
