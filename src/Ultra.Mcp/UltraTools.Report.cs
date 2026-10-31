// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Ultra.Core.Report;

namespace Ultra.Mcp;

internal static partial class UltraTools
{
    [McpServerTool(Name = "get_profile_report", ReadOnly = true, Idempotent = true)]
    [Description(@"Returns the text report of an ultra profile file (.json.gz or .json), the same as `ultra report`: the CPU time of each thread and its share of the process, the top leaf functions over all threads, and the hardware counters per function when the profile has some. It streams the file without loading it in memory: a good first look at a profile of any size, before load_profile and the drill-down tools.")]
    public static async Task<string> GetProfileReport(
        [Description("Absolute path to a profile .json.gz or .json file.")] string path,
        [Description("How many threads and functions to list. Default 12.")] int top = 12,
        [Description("Only the threads whose name contains this text.")] string? thread = null,
        [Description("Also the hot code offsets of the top 3 functions (where in a function's code the samples are). Default false.")] bool codeOffsets = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new McpException($"Ultra profile not found: {path}");
        }

        var report = new UltraProfileReport(Math.Clamp(top, 1, MaxAllowedResults), codeOffsets, thread);
        var writer = new StringWriter();
        try
        {
            await Task.Run(() => report.Run(path, writer), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not McpException)
        {
            throw new McpException(ex.Message, ex);
        }

        return writer.ToString();
    }
}
