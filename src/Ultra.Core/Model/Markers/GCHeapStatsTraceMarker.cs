// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a garbage collection heap stats event marker payload for Firefox Profiler.
/// </summary>
public sealed record GCHeapStatsTraceMarker : UTraceMarker
{
    /// <summary>
    /// Gets or sets the total heap size.
    /// </summary>
    public long TotalHeapSize { get; set; }

    /// <summary>
    /// Gets or sets the total promoted size.
    /// </summary>
    public long TotalPromoted { get; set; }

    /// <summary>
    /// Gets or sets the size of generation 0.
    /// </summary>
    public long GenerationSize0 { get; set; }

    /// <summary>
    /// Gets or sets the total promoted size of generation 0.
    /// </summary>
    public long TotalPromotedSize0 { get; set; }

    /// <summary>
    /// Gets or sets the size of generation 1.
    /// </summary>
    public long GenerationSize1 { get; set; }

    /// <summary>
    /// Gets or sets the total promoted size of generation 1.
    /// </summary>
    public long TotalPromotedSize1 { get; set; }

    /// <summary>
    /// Gets or sets the size of generation 2.
    /// </summary>
    public long GenerationSize2 { get; set; }

    /// <summary>
    /// Gets or sets the total promoted size of generation 2.
    /// </summary>
    public long TotalPromotedSize2 { get; set; }

    /// <summary>
    /// Gets or sets the size of generation 3.
    /// </summary>
    public long GenerationSize3 { get; set; }

    /// <summary>
    /// Gets or sets the total promoted size of generation 3.
    /// </summary>
    public long TotalPromotedSize3 { get; set; }

    /// <summary>
    /// Gets or sets the size of generation 4.
    /// </summary>
    public long GenerationSize4 { get; set; }

    /// <summary>
    /// Gets or sets the total promoted size of generation 4.
    /// </summary>
    public long TotalPromotedSize4 { get; set; }

    /// <summary>
    /// Gets or sets the finalization promoted size.
    /// </summary>
    public long FinalizationPromotedSize { get; set; }

    /// <summary>
    /// Gets or sets the finalization promoted count.
    /// </summary>
    public long FinalizationPromotedCount { get; set; }

    /// <summary>
    /// Gets or sets the pinned object count.
    /// </summary>
    public int PinnedObjectCount { get; set; }

    /// <summary>
    /// Gets or sets the sink block count.
    /// </summary>
    public int SinkBlockCount { get; set; }

    /// <summary>
    /// Gets or sets the GC handle count.
    /// </summary>
    public int GCHandleCount { get; set; }
}