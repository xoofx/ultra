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
    [Description(@"Returns the text report of an ultra profile file (.json.gz or .json), the same as `ultra report`: the CPU time of each thread and its share of the process, the top leaf functions over all threads, the hardware counters per function when the profile has some, and for a .NET process the GC (collections per generation and reason, GC pauses, heap) and the allocations per thread and type (sampled about every 100 KB), the lock contention of a --locks profile (waits per thread, waiting code, owners). It streams the file without loading it in memory: a good first look at a profile of any size, before load_profile and the drill-down tools. compact=true gives the short text meant for agents (each list with its total and what it leaves out); detailed=true adds the call trees over the whole file, with the semantics of call_tree and call_tree_inverted: top-down for the busiest threads, the callers of the top functions, or of focus= with its callees, over all the threads.")]
    public static async Task<string> GetProfileReport(
        [Description("Absolute path to a profile .json.gz or .json file.")] string path,
        [Description("How many threads and functions to list. Default 12.")] int top = 12,
        [Description("Only the threads whose name contains this text.")] string? thread = null,
        [Description("Also the hot code offsets of the top 3 functions (where in a function's code the samples are). Default false.")] bool codeOffsets = false,
        [Description("The compact text for agents: short section headers, the units in the column names, unpadded rows, a total and an omitted line per list, function names without parameters unless an overload needs them. Default false.")] bool compact = false,
        [Description("Also the call trees (samples, inclusive and self, % of each tree): top-down for the busiest threads, bottom-up (callers) for the top functions by self samples. Default false.")] bool detailed = false,
        [Description("With detailed: how many of the busiest threads get a top-down tree. Default 3.")] int treeThreads = 3,
        [Description("With detailed: how many of the top functions by self samples get a bottom-up tree. Default 3.")] int hotspots = 3,
        [Description("A function name substring: its callers (bottom-up) and callees (top-down) over all the threads instead of the top functions; implies detailed.")] string? focus = null,
        [Description("In the trees, the frames of a function from its outermost to its innermost call on a stack are one (A > F > B > F > C becomes A > F > C): for recursive code. Default false.")] bool collapseRecursion = false,
        [Description("Max rows from the root to the deepest row of a tree (default 12).")] int? maxDepth = null,
        [Description("Leave out the tree rows below this inclusive percent of their tree (default 1.0); they are summed in the omitted lines.")] double? minPercent = null,
        [Description("Max rows per tree, the heaviest first (default 40).")] int? maxRows = null,
        [Description("Optional inclusive lower bound on sample and marker time (trace-relative ms, same scale as query_markers).")] double? startMs = null,
        [Description("Optional inclusive upper bound on sample and marker time (trace-relative ms).")] double? endMs = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new McpException($"Ultra profile not found: {path}");
        }

        var report = new UltraProfileReport(new UltraProfileReportOptions
        {
            Top = Math.Clamp(top, 1, MaxAllowedResults),
            CodeOffsets = codeOffsets,
            ThreadFilter = thread,
            Compact = compact,
            Detailed = detailed,
            TreeThreads = Math.Clamp(treeThreads, 0, MaxAllowedResults),
            Hotspots = Math.Clamp(hotspots, 0, MaxAllowedResults),
            Focus = focus,
            CollapseRecursion = collapseRecursion,
            MaxDepth = maxDepth ?? 12,
            MinPercent = minPercent ?? 1.0,
            MaxRows = Math.Clamp(maxRows ?? 40, 1, MaxAllowedResults),
            StartMs = startMs,
            EndMs = endMs,
        });
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
