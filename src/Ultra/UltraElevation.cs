// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ultra.Core;
using Ultra.Core.Automation;

namespace Ultra;

/// <summary>
/// `--elevate` on Windows: ultra relaunches itself as administrator through a UAC prompt and relays the elevated ultra
/// to its own console.
/// </summary>
/// <remarks>
/// <para>
/// A process that is not elevated cannot read the output of an elevated process (ShellExecute with the <c>runas</c> verb
/// does not redirect), cannot send it Ctrl+C and cannot kill it. They communicate instead through a directory (the channel)
/// that the non-elevated ultra creates in its temp folder:
/// </para>
/// <list type="bullet">
/// <item><c>request.json</c>: the arguments, working directory and environment of the command, written by the parent.</item>
/// <item><c>output.jsonl</c>: the output of the elevated ultra (see <see cref="UltraChannelOutput"/>), replayed by the parent.</item>
/// <item><c>stop</c> / <c>abort</c>: created by the parent on the first / second Ctrl+C.</item>
/// <item><c>launch-N.json</c> / <c>launched-N.json</c>: the elevated ultra asks the parent to start the program to profile, so that
/// it runs as the user (not elevated) with the console of the parent, and the parent answers with its pid.</item>
/// </list>
/// <para>The elevated ultra also stops profiling (and still writes the profile) when the parent exits.</para>
/// </remarks>
internal static class UltraElevation
{
    /// <summary>The hidden command run by the elevated ultra.</summary>
    public const string ChildCommandName = "elevated-run";

    private const string RequestFileName = "request.json";
    private const string OutputFileName = "output.jsonl";
    private const string StopFileName = "stop";
    private const string AbortFileName = "abort";
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the user declined the UAC prompt

    /// <summary>
    /// The elevated side of the channel when this ultra was started by `--elevate`, otherwise <c>null</c>.
    /// </summary>
    public static UltraElevationChild? Child { get; private set; }

    /// <summary>
    /// Removes <c>--elevate</c> from the arguments of ultra (not from the arguments of the program after <c>--</c>).
    /// </summary>
    public static List<string> RemoveElevateOption(IEnumerable<string> arguments)
    {
        var result = new List<string>();
        bool afterSeparator = false;
        foreach (var argument in arguments)
        {
            if (!afterSeparator && argument == "--")
            {
                afterSeparator = true;
            }
            else if (!afterSeparator && argument is "--elevate" or "-elevate" or "/elevate")
            {
                continue;
            }

            result.Add(argument);
        }

        return result;
    }

    /// <summary>
    /// Runs a command in an elevated ultra (UAC prompt) and relays it: its output, the program it launches, Ctrl+C.
    /// </summary>
    /// <param name="arguments">The full arguments of ultra (<c>--elevate</c> is removed).</param>
    /// <param name="output">The output of this console.</param>
    /// <param name="consoleMode">How the output of a launched program is shown.</param>
    /// <returns>The exit code of the elevated ultra, or 1 when the elevation failed.</returns>
    public static async Task<int> RunElevatedAsync(IEnumerable<string> arguments, UltraOutput output, UltraProfilerConsoleMode consoleMode)
    {
        var channelDirectory = Path.Combine(Path.GetTempPath(), "ultra", "elevate", $"{Environment.ProcessId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(channelDirectory);
        try
        {
            var request = new UltraElevationRequest
            {
                Arguments = RemoveElevateOption(arguments),
                WorkingDirectory = Environment.CurrentDirectory,
                ParentProcessId = Environment.ProcessId,
            };

            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                request.Environment[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
            }

            WriteJson(Path.Combine(channelDirectory, RequestFileName), request, UltraElevationJsonContext.Default.UltraElevationRequest);

            var startInfo = UltraSelfCommand.ForCurrentProcess().CreateStartInfo([ChildCommandName, channelDirectory]);
            startInfo.UseShellExecute = true;
            startInfo.Verb = "runas";
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.WorkingDirectory = Environment.CurrentDirectory;

            output.Info("Requesting administrator rights for profiling: accept the Windows UAC prompt");
            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                throw new UltraElevationException("The administrator rights were declined (UAC prompt): profiling on Windows requires them");
            }

            if (process is null)
            {
                throw new UltraElevationException("Unable to start the elevated ultra");
            }

            using (process)
            using (var channel = new ParentChannel(channelDirectory, output, consoleMode))
            {
                ConsoleCancelEventHandler onCancel = (_, e) =>
                {
                    e.Cancel = true;
                    channel.RequestStop();
                };
                Console.CancelKeyPress += onCancel;
                try
                {
                    await output.RunAsync("Waiting for the elevated ultra", async () =>
                    {
                        while (!process.HasExited)
                        {
                            channel.Poll();
                            await Task.Delay(50);
                        }

                        channel.Poll();
                    });
                }
                finally
                {
                    Console.CancelKeyPress -= onCancel;
                }

                return process.ExitCode;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(channelDirectory, true);
            }
            catch (IOException)
            {
                // A file still in use: leave it in the temp folder
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Runs the command requested through a channel, in the elevated ultra.
    /// </summary>
    /// <param name="channelDirectory">The channel directory.</param>
    /// <param name="runCommand">Runs the ultra command line with the requested arguments.</param>
    /// <returns>The exit code of the command.</returns>
    public static async Task<int> RunChildAsync(string channelDirectory, Func<string[], Task<int>> runCommand)
    {
        var request = ReadJson(Path.Combine(channelDirectory, RequestFileName), UltraElevationJsonContext.Default.UltraElevationRequest)
                      ?? throw new InvalidOperationException($"Invalid elevation request in {channelDirectory}");

        // The elevated process starts with the environment of the administrator profile and in the system folder:
        // restore the ones of the user so that relative paths and environment variables behave as in the user's console
        Directory.SetCurrentDirectory(request.WorkingDirectory);
        foreach (var (name, value) in request.Environment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        using var output = new UltraChannelOutput(Path.Combine(channelDirectory, OutputFileName));
        Child = new UltraElevationChild(channelDirectory, request.ParentProcessId, output);
        try
        {
            return await runCommand(request.Arguments.ToArray());
        }
        catch (Exception ex)
        {
            output.Error($"Unexpected error: {ex.Message}");
            return 1;
        }
        finally
        {
            Child = null;
        }
    }

    internal static void WriteJson<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        // Write then move, so that the other side never reads a partial file
        var tempPath = $"{path}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, typeInfo));
        File.Move(tempPath, path, true);
    }

    internal static T? ReadJson<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo);

    /// <summary>
    /// The non-elevated side of the channel: replays the output of the elevated ultra and starts the programs it asks for.
    /// </summary>
    private sealed class ParentChannel(string channelDirectory, UltraOutput output, UltraProfilerConsoleMode consoleMode) : IDisposable
    {
        private readonly List<Process> _programs = new();
        private readonly HashSet<string> _handledLaunches = new(StringComparer.OrdinalIgnoreCase);
        private readonly StringBuilder _pendingText = new();
        private FileStream? _outputStream;
        private StreamReader? _outputReader;

        public void RequestStop()
        {
            var stopFile = Path.Combine(channelDirectory, StopFileName);
            if (!File.Exists(stopFile))
            {
                File.WriteAllText(stopFile, "stop");
                output.Warning("Stop requested via CTRL+C: the profile is still written. Press CTRL+C again to abort");
            }
            else
            {
                File.WriteAllText(Path.Combine(channelDirectory, AbortFileName), "abort");
                output.Warning("Abort requested via CTRL+C");
            }
        }

        public void Poll()
        {
            ReadOutput();
            HandleLaunchRequests();
        }

        private void ReadOutput()
        {
            if (_outputReader is null)
            {
                var path = Path.Combine(channelDirectory, OutputFileName);
                if (!File.Exists(path))
                {
                    return;
                }

                _outputStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _outputReader = new StreamReader(_outputStream, Encoding.UTF8);
            }

            // Only complete lines are replayed: the elevated ultra may be writing the next one
            var text = _outputReader.ReadToEnd();
            if (text.Length == 0)
            {
                return;
            }

            _pendingText.Append(text);
            var all = _pendingText.ToString();
            var lastNewLine = all.LastIndexOf('\n');
            if (lastNewLine < 0)
            {
                return;
            }

            foreach (var line in all.AsSpan(0, lastNewLine).ToString().Split('\n'))
            {
                if (line.Length > 0 && !UltraChannelMessage.Replay(line, output))
                {
                    output.Text(line);
                }
            }

            _pendingText.Clear();
            _pendingText.Append(all, lastNewLine + 1, all.Length - lastNewLine - 1);
        }

        private void HandleLaunchRequests()
        {
            foreach (var requestFile in Directory.EnumerateFiles(channelDirectory, "launch-*.json"))
            {
                if (!_handledLaunches.Add(requestFile))
                {
                    continue;
                }

                var id = Path.GetFileNameWithoutExtension(requestFile).Substring("launch-".Length);
                var response = new UltraLaunchResponse();
                try
                {
                    var request = ReadJson(requestFile, UltraElevationJsonContext.Default.UltraLaunchRequest)
                                  ?? throw new InvalidOperationException("Invalid launch request");
                    response.ProcessId = StartProgram(request);
                }
                catch (Exception ex)
                {
                    response.Error = ex.Message;
                }

                WriteJson(Path.Combine(channelDirectory, $"launched-{id}.json"), response, UltraElevationJsonContext.Default.UltraLaunchResponse);
            }
        }

        private int StartProgram(UltraLaunchRequest request)
        {
            // Same console behavior as ultra when it starts the program itself (see UltraProfiler.StartProcess)
            var startInfo = new ProcessStartInfo(request.FileName)
            {
                WorkingDirectory = request.WorkingDirectory,
            };
            foreach (var argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process { StartInfo = startInfo };
            if (consoleMode == UltraProfilerConsoleMode.Silent)
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
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null) output.ProgramOutput(e.Data, false);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null) output.ProgramOutput(e.Data, true);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            _programs.Add(process);
            return process.Id;
        }

        public void Dispose()
        {
            _outputReader?.Dispose();
            _outputStream?.Dispose();
            foreach (var process in _programs)
            {
                process.Dispose();
            }
        }
    }
}

/// <summary>
/// The elevated side of the channel: its output, its stop requests and the start of the program to profile by the parent.
/// </summary>
internal sealed class UltraElevationChild(string channelDirectory, int parentProcessId, UltraOutput output)
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(60);
    private int _launchId;

    /// <summary>The output relayed to the parent console.</summary>
    public UltraOutput Output { get; } = output;

    /// <summary>The file created by the parent on the first Ctrl+C.</summary>
    public string StopFile { get; } = Path.Combine(channelDirectory, "stop");

    /// <summary>The file created by the parent on the second Ctrl+C.</summary>
    public string AbortFile { get; } = Path.Combine(channelDirectory, "abort");

    /// <summary>The pid of the non-elevated ultra.</summary>
    public int ParentProcessId { get; } = parentProcessId;

    /// <summary>
    /// Asks the non-elevated ultra to start the program (see <see cref="UltraProfilerOptions.ProgramStarter"/>).
    /// </summary>
    public async Task<Process> StartProgramAsync(ProcessStartInfo startInfo)
    {
        var id = Interlocked.Increment(ref _launchId);
        var request = new UltraLaunchRequest
        {
            FileName = startInfo.FileName,
            Arguments = startInfo.ArgumentList.ToList(),
            WorkingDirectory = string.IsNullOrEmpty(startInfo.WorkingDirectory) ? Environment.CurrentDirectory : startInfo.WorkingDirectory,
        };
        UltraElevation.WriteJson(Path.Combine(channelDirectory, $"launch-{id}.json"), request, UltraElevationJsonContext.Default.UltraLaunchRequest);

        var responseFile = Path.Combine(channelDirectory, $"launched-{id}.json");
        var clock = Stopwatch.StartNew();
        while (!File.Exists(responseFile))
        {
            if (clock.Elapsed > LaunchTimeout)
            {
                throw new InvalidOperationException($"The non-elevated ultra did not start {startInfo.FileName}");
            }

            await Task.Delay(5);
        }

        var response = UltraElevation.ReadJson(responseFile, UltraElevationJsonContext.Default.UltraLaunchResponse)
                       ?? throw new InvalidOperationException("Invalid launch response");
        if (response.Error is not null)
        {
            throw new InvalidOperationException($"Unable to start {startInfo.FileName}: {response.Error}");
        }

        try
        {
            return Process.GetProcessById(response.ProcessId);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"The program {startInfo.FileName} (pid {response.ProcessId}) exited before the profiler could attach to it");
        }
    }
}

/// <summary>
/// An error of `--elevate` (declined UAC prompt...).
/// </summary>
internal sealed class UltraElevationException(string message) : Exception(message);

internal sealed class UltraElevationRequest
{
    public List<string> Arguments { get; set; } = new();

    public string WorkingDirectory { get; set; } = string.Empty;

    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int ParentProcessId { get; set; }
}

internal sealed class UltraLaunchRequest
{
    public string FileName { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = new();

    public string WorkingDirectory { get; set; } = string.Empty;
}

internal sealed class UltraLaunchResponse
{
    public int ProcessId { get; set; }

    public string? Error { get; set; }
}

[JsonSerializable(typeof(UltraElevationRequest))]
[JsonSerializable(typeof(UltraLaunchRequest))]
[JsonSerializable(typeof(UltraLaunchResponse))]
internal partial class UltraElevationJsonContext : JsonSerializerContext;
