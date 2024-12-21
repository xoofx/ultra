// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a garbage collection event marker payload for Firefox Profiler.
/// </summary>
public record GCTraceMarker : UTraceMarker
{
    /// <summary>
    /// Gets or sets the reason for the garbage collection.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets the count of garbage collections.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets the depth of the garbage collection.
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    /// Gets or sets the type of garbage collection.
    /// </summary>
    public string? GCType { get; set; }
}