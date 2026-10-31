// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Reflection;

namespace Ultra.Core.Automation;

/// <summary>
/// How to start the ultra command line of the current process again: used to relaunch ultra elevated (`--elevate`)
/// and by the MCP server (`ultra mcp`) to run profiling jobs.
/// </summary>
internal sealed class UltraSelfCommand
{
    /// <summary>
    /// Creates a command starting <paramref name="fileName"/> with <paramref name="prefixArguments"/> before the arguments of each command.
    /// </summary>
    /// <param name="fileName">The executable to start.</param>
    /// <param name="prefixArguments">The arguments passed before the ultra arguments (e.g. the path of ultra.dll for the dotnet host).</param>
    public UltraSelfCommand(string fileName, IReadOnlyList<string> prefixArguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(prefixArguments);
        FileName = fileName;
        PrefixArguments = prefixArguments;
    }

    /// <summary>
    /// The executable to start.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// The arguments passed before the ultra arguments.
    /// </summary>
    public IReadOnlyList<string> PrefixArguments { get; }

    /// <summary>
    /// Gets the command to start the ultra of the current process: <c>ultra.exe</c> (the apphost of the global tool),
    /// or <c>dotnet ultra.dll</c> when ultra runs through the dotnet host.
    /// </summary>
    /// <exception cref="InvalidOperationException">The path of the current process is not available.</exception>
    public static UltraSelfCommand ForCurrentProcess()
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The path of the current process is not available");
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryName = Assembly.GetEntryAssembly()?.GetName().Name ?? "ultra";
            return new UltraSelfCommand(processPath, [Path.Combine(AppContext.BaseDirectory, $"{entryName}.dll")]);
        }

        return new UltraSelfCommand(processPath, []);
    }

    /// <summary>
    /// Creates the start info running ultra with the specified arguments.
    /// </summary>
    /// <param name="arguments">The ultra arguments (e.g. <c>profile --pid 1234</c>).</param>
    /// <returns>A new start info.</returns>
    public ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(FileName);
        foreach (var argument in PrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>
    /// Formats a command line for display, quoting the arguments that need it.
    /// </summary>
    /// <param name="fileName">The executable.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The command line.</returns>
    public static string FormatCommandLine(string fileName, IEnumerable<string> arguments)
        => string.Join(" ", new[] { fileName }.Concat(arguments).Select(Quote));

    private static string Quote(string argument)
        => argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"') ? argument : $"\"{argument.Replace("\"", "\\\"")}\"";
}
