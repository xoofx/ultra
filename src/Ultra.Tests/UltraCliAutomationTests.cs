// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Tests;

/// <summary>
/// Tests of the automation pieces of the ultra command line: the channel of `--elevate` and the stop file.
/// </summary>
[TestClass]
public class UltraCliAutomationTests
{
    [TestMethod]
    public void ElevateOptionIsRemovedOnlyBeforeTheProgram()
    {
        var arguments = Ultra.UltraElevation.RemoveElevateOption(["--verbose", "profile", "--elevate", "--duration", "5", "--", "app.exe", "--elevate"]);
        CollectionAssert.AreEqual(new[] { "--verbose", "profile", "--duration", "5", "--", "app.exe", "--elevate" }, arguments);
    }

    /// <summary>
    /// The elevated ultra writes its output as JSON lines that the non-elevated ultra replays on its console, markup included.
    /// </summary>
    [TestMethod]
    public void ChannelOutputIsReplayed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ultra-tests-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var channel = new UltraChannelOutput(path))
            {
                channel.Status("Profiling - 1s");
                channel.Step("Merging ETL Files");
                channel.Error("Bad [thing]");
                channel.ProgramOutput("hello \"world\"\twith\ttabs", false);
                channel.ProgramOutput("oops", true);
            }

            var recording = new RecordingOutput();
            foreach (var line in File.ReadAllLines(path))
            {
                Assert.IsTrue(UltraChannelMessage.Replay(line, recording), $"Invalid line {line}");
            }

            Assert.IsFalse(UltraChannelMessage.Replay("not json", recording));
            Assert.IsFalse(UltraChannelMessage.Replay("""{"k":"unknown","t":"x"}""", recording));

            CollectionAssert.AreEqual(new[]
            {
                "status:Profiling - 1s",
                "step:Merging ETL Files",
                "markup:[red]Bad [[thing]][/]",
                "out:hello \"world\"\twith\ttabs",
                "err:oops",
            }, recording.Calls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task StopFileStopsProfiling()
    {
        var stopFile = Path.Combine(Path.GetTempPath(), $"ultra-tests-{Guid.NewGuid():N}.stop");
        // A stop file left by a previous run is deleted at start
        await File.WriteAllTextAsync(stopFile, "old");
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var monitor = UltraStopMonitor.Start(stopFile, null, reason => stopped.TrySetResult(reason), () => { });
            Assert.IsNotNull(monitor);
            Assert.IsFalse(File.Exists(stopFile));

            await Task.Delay(400);
            Assert.IsFalse(stopped.Task.IsCompleted);

            await File.WriteAllTextAsync(stopFile, "stop");
            var reason = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            StringAssert.Contains(reason, "Stop requested by the stop file");
        }
        finally
        {
            File.Delete(stopFile);
        }

        Assert.IsNull(UltraStopMonitor.Start(null, null, _ => { }, () => { }));
    }

    /// <summary>
    /// The live output (interactive terminal) runs the work under an inline live region, writes the lines of the work above it
    /// and, with `--mode live`, keeps the last lines of the program visible after the region is removed.
    /// </summary>
    [TestMethod]
    public async Task LiveOutputWritesTheLinesOfTheWork()
    {
        var backend = new XenoAtom.Terminal.Backends.InMemoryTerminalBackend(new XenoAtom.Terminal.TerminalSize(100, 30));
        using (XenoAtom.Terminal.Terminal.Open(backend))
        {
            var output = new UltraLiveOutput(showProgramOutput: true, verbose: true);
            await output.RunAsync("Starting", async () =>
            {
                output.Step("Step one");
                output.Status("Profiling - 1s");
                output.Info("A [green] line");
                output.ProgramOutput("program line", false);
                await Task.Delay(300);
                output.Step("Step two");
            });

            output.Text("After the live region");
        }

        var text = backend.GetOutText();
        StringAssert.Contains(text, "A [green] line");
        StringAssert.Contains(text, "Step one");
        StringAssert.Contains(text, "Step two");
        StringAssert.Contains(text, "program line");
        StringAssert.Contains(text, "After the live region");
    }

    /// <summary>
    /// The plain output (redirected: scripts, agents) prints every step but collapses a step repeated with a new percentage,
    /// and prints the status at most every few seconds.
    /// </summary>
    [TestMethod]
    public void PlainOutputPrintsStepsAndThrottlesTheStatus()
    {
        var backend = new XenoAtom.Terminal.Backends.InMemoryTerminalBackend(new XenoAtom.Terminal.TerminalSize(100, 30));
        using (XenoAtom.Terminal.Terminal.Open(backend))
        {
            var output = new UltraPlainOutput(">>ultra::", verbose: false);
            output.Step("Loading perf events 10%");
            output.Step("Loading perf events 20%");
            output.Step("Converting ETL file fast filtered for process 12");
            output.Step("Converting to Firefox Profiler JSON a.json.gz");
            output.Status("Profiling - 1s");
            output.Status("Profiling - 2s");
            output.ProgramOutput("program line", false);
        }

        var lines = backend.GetOutText().Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        CollectionAssert.AreEqual(new[]
        {
            ">>ultra::Loading perf events 10%",
            ">>ultra::Converting ETL file fast filtered for process 12",
            ">>ultra::Converting to Firefox Profiler JSON a.json.gz",
            "program line",
        }, lines);
    }

    [TestMethod]
    public async Task LiveOutputPropagatesTheErrorOfTheWork()
    {
        var backend = new XenoAtom.Terminal.Backends.InMemoryTerminalBackend(new XenoAtom.Terminal.TerminalSize(100, 30));
        using (XenoAtom.Terminal.Terminal.Open(backend))
        {
            var output = new UltraLiveOutput(showProgramOutput: false, verbose: false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => output.RunAsync("Starting", () => throw new InvalidOperationException("boom")));
        }
    }

    private sealed class RecordingOutput : UltraOutput
    {
        public List<string> Calls { get; } = new();

        public override void MarkupLine(string markup) => Calls.Add($"markup:{markup}");

        public override void Status(string text) => Calls.Add($"status:{text}");

        public override void Step(string text) => Calls.Add($"step:{text}");

        public override void ProgramOutput(string line, bool isError) => Calls.Add($"{(isError ? "err" : "out")}:{line}");
    }
}
