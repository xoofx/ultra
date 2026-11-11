// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Report;

/// <summary>
/// The options of an <see cref="UltraProfileReport"/>.
/// </summary>
public sealed class UltraProfileReportOptions
{
    /// <summary>How many threads and functions to list. Default 12.</summary>
    public int Top { get; set; } = 12;

    /// <summary>Also the hot code offsets of the top 3 functions (the frame addresses: offsets in the function's code).</summary>
    public bool CodeOffsets { get; set; }

    /// <summary>Only the threads whose name contains this, when given.</summary>
    public string? ThreadFilter { get; set; }

    /// <summary>The parsing threads (default: half the processors).</summary>
    public int Workers { get; set; }

    /// <summary>
    /// The compact text for agents: short section headers, the units in the column names, unpadded rows with the name last, the
    /// totals of each list and what it leaves out, and the function names without their parameters unless an overload needs them.
    /// </summary>
    public bool Compact { get; set; }

    /// <summary>Also the call trees: top-down for the busiest threads, bottom-up (the callers) for the top functions or <see cref="Focus"/>.</summary>
    public bool Detailed { get; set; }

    /// <summary>How many of the busiest threads get a top-down tree. Default 3.</summary>
    public int TreeThreads { get; set; } = 3;

    /// <summary>How many of the top functions by self samples get a bottom-up tree (their callers). Default 3.</summary>
    public int Hotspots { get; set; } = 3;

    /// <summary>
    /// The functions whose name contains this (case-insensitive) get the bottom-up tree (their callers) and a top-down tree (their
    /// callees) over all the threads, instead of the top functions.
    /// </summary>
    public string? Focus { get; set; }

    /// <summary>
    /// Collapses the recursion in the trees: a function's frames from its outermost to its innermost call on a stack become one
    /// (A > F > B > F > C becomes A > F > C), so every function is once on a path and a recursive algorithm shows what its levels do
    /// together instead of one row per level. The leaf stays the leaf: the self samples do not change.
    /// </summary>
    public bool CollapseRecursion { get; set; }

    /// <summary>The rows from the root to the deepest row of a tree. Default 12.</summary>
    public int MaxDepth { get; set; } = 12;

    /// <summary>The rows under this percent of their tree are left out (summed in the omitted lines). Default 1.</summary>
    public double MinPercent { get; set; } = 1.0;

    /// <summary>The rows of a tree at most, the heaviest first. Default 40.</summary>
    public int MaxRows { get; set; } = 40;

    /// <summary>Only the samples and markers from this time (ms from the start of the profile, the scale of the MCP tools).</summary>
    public double? StartMs { get; set; }

    /// <summary>Only the samples and markers up to this time (ms from the start of the profile).</summary>
    public double? EndMs { get; set; }
}
