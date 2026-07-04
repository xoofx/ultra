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
    public required long TotalHeapSize { get; set; }

    /// <summary>
    /// Gets or sets the total promoted size.
    /// </summary>
    public required long TotalPromoted { get; set; }

    /// <summary>
    /// Gets or sets the size of generation 0.
    /// </summary>
    public required long GenerationSize0 { get; set; }

    /// <summary>
    /// Gets or sets the total promoted size of generation 0.
    /// </summary>
    public required long TotalPromotedSize0 { get; set; }

    /// <summary>
    /// Gets or sets the size of generation 1.
    /// </summary>
    public required long GenerationSize1 { get; set; }

    /// <summary>
    /// Gets or sets the total promoted size of generation 1.
    /// </summary>
    public required long TotalPromotedSize1 { get; set; }

    /// <summary>
    /// Gets or sets the size of generation 2.
    /// </summary>
    public required long GenerationSize2 { get; set; }

    /// <summary>
    /// Gets or sets the total promoted size of generation 2.
    /// </summary>
    public required long TotalPromotedSize2 { get; set; }

    /// <summary>
    /// Gets or sets the size of generation 3.
    /// </summary>
    public required long GenerationSize3 { get; set; }

    /// <summary>
    /// Gets or sets the total promoted size of generation 3.
    /// </summary>
    public required long TotalPromotedSize3 { get; set; }

    /// <summary>
    /// Gets or sets the size of generation 4.
    /// </summary>
    public required long GenerationSize4 { get; set; }

    /// <summary>
    /// Gets or sets the total promoted size of generation 4.
    /// </summary>
    public required long TotalPromotedSize4 { get; set; }

    /// <summary>
    /// Gets or sets the finalization promoted size.
    /// </summary>
    public required long FinalizationPromotedSize { get; set; }

    /// <summary>
    /// Gets or sets the finalization promoted count.
    /// </summary>
    public required long FinalizationPromotedCount { get; set; }

    /// <summary>
    /// Gets or sets the pinned object count.
    /// </summary>
    public required int PinnedObjectCount { get; set; }

    /// <summary>
    /// Gets or sets the sink block count.
    /// </summary>
    public required int SinkBlockCount { get; set; }

    /// <summary>
    /// Gets or sets the GC handle count.
    /// </summary>
    public required int GCHandleCount { get; set; }
}