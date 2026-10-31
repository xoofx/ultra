// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;
using System.Text.RegularExpressions;
using Ultra.Core;
using Ultra.Core.Automation;
using Ultra.Mcp;

namespace Ultra.Tests;

/// <summary>
/// Tests of the MCP server of ultra (`ultra mcp`).
/// </summary>
[TestClass]
public class UltraMcpTests
{
    [TestMethod]
    public void ServerExposesTheAnalysisAndCaptureTools()
    {
        var tools = UltraMcpServer.CreateTools();
        var names = tools.Select(x => x.ProtocolTool.Name).Order().ToList();

        string[] expected =
        [
            "call_tree", "call_tree_inverted", "counter_samples", "find_hotspots", "find_processes", "focus_function", "get_call_stack",
            "get_job_status", "get_llm_guide", "get_profile_report", "get_profile_summary", "get_sample", "list_counters", "list_jobs",
            "list_loaded_profiles", "list_samples", "list_threads", "load_profile", "module_breakdown", "query_markers", "reload_profile",
            "start_conversion", "start_profiling", "stop_job", "top_functions", "unload_all_profiles", "unload_profile",
        ];
        CollectionAssert.AreEqual(expected, names);

        foreach (var tool in tools)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description), $"The tool {tool.ProtocolTool.Name} has no description");
        }

        var options = UltraMcpServer.CreateOptions("1.0.0");
        Assert.AreEqual("ultra", options.ServerInfo!.Name);
        StringAssert.Contains(options.ServerInstructions, "start_profiling");
        Assert.AreEqual(expected.Length, options.ToolCollection!.Count);
    }

    [TestMethod]
    public void LlmGuideDescribesCaptureAndAnalysis()
    {
        var guide = UltraHelp.GetLlmGuide();
        StringAssert.Contains(guide, "start_profiling");
        StringAssert.Contains(guide, "call_tree");
    }

    /// <summary>
    /// A profiling job runs ultra with a result file and a stop file: stop_job creates the stop file, and the status of the job
    /// then reads the profiles of the result file. A fake ultra (a script) stands for the real one, which needs administrator rights.
    /// </summary>
    [TestMethod]
    public async Task ProfilingJobStopsAndReportsItsProfiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ultra-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            UltraJobs.Configure(CreateFakeUltra(directory));

            var started = UltraJobs.StartProfiling(pids: [Environment.ProcessId], durationSeconds: 30, elevate: false);
            StringAssert.Contains(started, "state: running");
            StringAssert.Contains(started, $"command: ultra profile --pid {Environment.ProcessId} --duration 30");
            var jobId = int.Parse(Regex.Match(started, @"jobId: (\d+)").Groups[1].Value);

            // The job runs until it is stopped
            var running = await UltraJobs.GetJobStatus(jobId, waitSeconds: 0.5);
            StringAssert.Contains(running, "state: running");
            StringAssert.Contains(UltraJobs.ListJobs(), $"{jobId}\tprofile\trunning");

            var stopped = await UltraJobs.StopJob(jobId, waitSeconds: 60);
            StringAssert.Contains(stopped, "state: succeeded");
            StringAssert.Contains(stopped, "stopRequested: true");
            StringAssert.Contains(stopped, "exitCode: 0");
            StringAssert.Contains(stopped, Path.Combine(directory, "fake.json.gz"));
            StringAssert.Contains(stopped, "fake ultra started");
            StringAssert.Contains(stopped, "get_profile_report");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// `ultra convert` has no --stop-file: a conversion job gets only the result file (a regression: every conversion job failed).
    /// </summary>
    [TestMethod]
    public async Task ConversionJobHasNoStopFile()
    {
        UltraJobs.Configure(new UltraSelfCommand("dotnet", ["--version"]));
        var job = UltraJobs.StartJob("convert", ["convert", "--pid", "1", "trace.etl"], null);
        try
        {
            var arguments = job.Process.StartInfo.ArgumentList.ToList();
            CollectionAssert.AreEqual(new[] { "--version", "convert", "--result-file", job.ResultFile, "--pid", "1", "trace.etl" }, arguments);
            Assert.IsNull(job.StopFile);
        }
        finally
        {
            await job.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }

        var profileJob = UltraJobs.StartJob("profile", ["profile", "--pid", "1"], null);
        await profileJob.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        CollectionAssert.AreEqual(new[] { "--version", "profile", "--result-file", profileJob.ResultFile, "--stop-file", profileJob.StopFile, "--pid", "1" }, profileJob.Process.StartInfo.ArgumentList.ToList());
    }

    /// <summary>
    /// Without a working directory, a profiling job writes its profiles in the ultra profiles folder and a conversion job next to
    /// its trace, not in the working directory of the MCP server (often the repository of the agent: a regression).
    /// </summary>
    [TestMethod]
    public async Task JobsDoNotWriteInTheDirectoryOfTheServer()
    {
        UltraJobs.Configure(new UltraSelfCommand("dotnet", ["--version"]));

        var profiling = UltraJobs.GetJob(ParseJobId(UltraJobs.StartProfiling(program: Path.Combine("bin", "app.exe"), elevate: false)));
        await profiling.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(UltraJobs.GetDefaultProfilesDirectory(), profiling.Process.StartInfo.WorkingDirectory);
        Assert.IsTrue(Directory.Exists(profiling.Process.StartInfo.WorkingDirectory));
        // The relative program is resolved against the server, since the job runs elsewhere
        Assert.AreEqual(Path.GetFullPath(Path.Combine("bin", "app.exe")), profiling.Process.StartInfo.ArgumentList[^1]);

        var conversion = UltraJobs.GetJob(ParseJobId(UltraJobs.StartConversion(["trace.etl"], [1])));
        await conversion.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(Path.GetFullPath("trace.etl"), conversion.Process.StartInfo.ArgumentList[^1]);
        Assert.AreEqual(Path.GetDirectoryName(Path.GetFullPath("trace.etl")), conversion.Process.StartInfo.WorkingDirectory);
    }

    private static int ParseJobId(string description) => int.Parse(Regex.Match(description, @"jobId: (\d+)").Groups[1].Value);

    [TestMethod]
    public void StartProfilingRequiresPidsOrProgram()
    {
        var exception = Assert.ThrowsExactly<ModelContextProtocol.McpException>(() => UltraJobs.StartProfiling());
        StringAssert.Contains(exception.Message, "Either pids or program is required");
    }

    [TestMethod]
    public void FindProcessesFindsTheCurrentProcess()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var text = UltraJobs.FindProcesses(current.ProcessName, 5000);
        StringAssert.Contains(text, $"{current.Id}\t{current.ProcessName}");
    }

    [TestMethod]
    public async Task ProfileReportToolWritesTheReport()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ultra-tests-{Guid.NewGuid():N}.json");
        try
        {
            var profile = new FirefoxProfiler.Profile();
            profile.Meta.Product = "McpTest";
            profile.Meta.ProfilingEndTime = 1000;
            await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(profile, FirefoxProfiler.JsonProfilerContext.Default.Profile));

            var report = await UltraTools.GetProfileReport(path);
            StringAssert.Contains(report, "product=McpTest");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void CommandResultRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "ultra-tests", $"{Guid.NewGuid():N}", "result.json");
        try
        {
            var result = new UltraCommandResult
            {
                Command = "profile",
                Success = true,
                Stopped = true,
                Profiles = { new UltraCommandResultFile { Path = @"C:\work\a.json.gz", Size = 42 } },
            };
            result.WriteTo(path);

            var json = File.ReadAllText(path);
            StringAssert.Contains(json, "\"success\": true");
            StringAssert.Contains(json, "\"profiles\": [");
            Assert.IsFalse(json.Contains("\"error\""), "A null error is not written");

            var read = UltraCommandResult.TryRead(path);
            Assert.IsNotNull(read);
            Assert.AreEqual("profile", read.Command);
            Assert.IsTrue(read.Stopped);
            Assert.AreEqual(42, read.Profiles.Single().Size);

            Assert.IsNull(UltraCommandResult.TryRead(path + ".missing"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, true);
        }
    }

    [TestMethod]
    public void SelfCommandPrependsItsArguments()
    {
        var command = new UltraSelfCommand("dotnet", ["ultra.dll"]);
        var startInfo = command.CreateStartInfo(["profile", "--pid", "12"]);
        Assert.AreEqual("dotnet", startInfo.FileName);
        CollectionAssert.AreEqual(new[] { "ultra.dll", "profile", "--pid", "12" }, startInfo.ArgumentList.ToArray());
        Assert.AreEqual("ultra profile -- \"C:\\My App\\a.exe\" \"\"", UltraSelfCommand.FormatCommandLine("ultra", ["profile", "--", @"C:\My App\a.exe", ""]));
    }

    /// <summary>
    /// A fake ultra: prints a line, waits for the stop file (the 5th argument), then writes a profile and the result file (the 3rd).
    /// </summary>
    private static UltraSelfCommand CreateFakeUltra(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(directory, "fake-ultra.ps1");
            File.WriteAllText(script, $$"""
                $resultFile = $args[2]; $stopFile = $args[4]
                Write-Output "fake ultra started"
                $clock = [Diagnostics.Stopwatch]::StartNew()
                while (-not (Test-Path $stopFile) -and $clock.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 50 }
                $profilePath = '{{Path.Combine(directory, "fake.json.gz")}}'
                Set-Content -Path $profilePath -Value 'x'
                $json = '{"version":1,"command":"profile","success":true,"exitCode":0,"stopped":true,"profiles":[{"path":' + (ConvertTo-Json $profilePath) + ',"size":1}]}'
                Set-Content -Path $resultFile -Value $json
                Write-Output "fake ultra stopped"
                exit 0
                """);
            return new UltraSelfCommand("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script]);
        }

        var shellScript = $$"""
            result="$3"; stop="$5"
            echo "fake ultra started"
            i=0; while [ ! -f "$stop" ] && [ $i -lt 600 ]; do sleep 0.05; i=$((i+1)); done
            p='{{Path.Combine(directory, "fake.json.gz")}}'; printf x > "$p"
            printf '{"version":1,"command":"profile","success":true,"exitCode":0,"stopped":true,"profiles":[{"path":"%s","size":1}]}' "$p" > "$result"
            echo "fake ultra stopped"
            """;
        return new UltraSelfCommand("/bin/sh", ["-c", shellScript, "sh"]);
    }
}
