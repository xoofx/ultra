// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Ultra.Core;

namespace Ultra.Tests;

[TestClass]
public class UltraProfilerEventPipeTests
{
    /// <summary>
    /// When the injected sampler cannot be connected (it took more than 5s to boot on a starved machine), the profiling
    /// fails: the launched program must not be left running unprofiled (a regression: it was left orphaned).
    /// </summary>
    [TestMethod]
    public async Task FailedConnectionKillsTheLaunchedProgram()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) Assert.Inconclusive("This test is only for macOS ARM64");

        var previousTimeout = DiagnosticPortSession.SamplerConnectTimeout;
        DiagnosticPortSession.SamplerConnectTimeout = TimeSpan.Zero;
        try
        {
            var logs = new List<string>();
            var options = new UltraProfilerOptions
            {
                ProgramPath = "/bin/sleep",
                DurationInSeconds = 30,
                LogProgress = message => { lock (logs) logs.Add(message); },
            };
            options.Arguments.Add("60");

            using var profiler = UltraProfiler.Create();
            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => profiler.Run(options));
            StringAssert.Contains(exception.Message, "Cannot connect to the diagnostic port socket");

            var launchedPrograms = FindChildProcesses("sleep");
            foreach (var pid in launchedPrograms)
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
            }

            lock (logs) Assert.AreEqual(0, launchedPrograms.Count, $"The launched program is still running. Logs:{Environment.NewLine}{string.Join(Environment.NewLine, logs)}");
        }
        finally
        {
            DiagnosticPortSession.SamplerConnectTimeout = previousTimeout;
        }
    }

    private static List<int> FindChildProcesses(string name)
    {
        using var pgrep = Process.Start(new ProcessStartInfo("/usr/bin/pgrep", ["-P", Environment.ProcessId.ToString(), "-x", name]) { RedirectStandardOutput = true })!;
        var output = pgrep.StandardOutput.ReadToEnd();
        pgrep.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
    }
}
