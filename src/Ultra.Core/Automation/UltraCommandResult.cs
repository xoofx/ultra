// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ultra.Core.Automation;

/// <summary>
/// The JSON summary of an ultra command (`--result-file`), read by tools and agents that automate ultra.
/// </summary>
/// <remarks>
/// The JSON shape is a contract for external tools: only add properties, do not rename or remove them.
/// </remarks>
internal sealed class UltraCommandResult
{
    /// <summary>
    /// The version of this JSON format.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// The command that ran: <c>profile</c> or <c>convert</c>.
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> when the command succeeded and wrote its profile files.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The exit code of ultra.
    /// </summary>
    public int ExitCode { get; set; }

    /// <summary>
    /// The error message when the command failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// <c>true</c> when the profiling was stopped before its end (Ctrl+C, a stop file, the duration reached...).
    /// </summary>
    public bool Stopped { get; set; }

    /// <summary>
    /// <c>true</c> when the command ran in an elevated process (Windows administrator).
    /// </summary>
    public bool Elevated { get; set; }

    /// <summary>
    /// When the command started.
    /// </summary>
    public DateTimeOffset StartTime { get; set; }

    /// <summary>
    /// How long the command took, in seconds.
    /// </summary>
    public double DurationSeconds { get; set; }

    /// <summary>
    /// The Firefox Profiler JSON files written (one per process).
    /// </summary>
    public List<UltraCommandResultFile> Profiles { get; set; } = new();

    /// <summary>
    /// Writes this result as indented JSON to the specified file, creating its directory if needed.
    /// </summary>
    /// <param name="path">The path of the JSON file.</param>
    public void WriteTo(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write then move, so that a reader never sees a partial file
        var tempPath = $"{path}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, UltraAutomationJsonContext.Default.UltraCommandResult));
        File.Move(tempPath, path, true);
    }

    /// <summary>
    /// Reads a result JSON file.
    /// </summary>
    /// <param name="path">The path of the JSON file.</param>
    /// <returns>The result read, or <c>null</c> when the file does not exist or is not valid.</returns>
    public static UltraCommandResult? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), UltraAutomationJsonContext.Default.UltraCommandResult) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// A profile file written by an ultra command.
/// </summary>
internal sealed class UltraCommandResultFile
{
    /// <summary>
    /// The full path of the file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The size of the file in bytes.
    /// </summary>
    public long Size { get; set; }
}

[JsonSerializable(typeof(UltraCommandResult))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class UltraAutomationJsonContext : JsonSerializerContext;
