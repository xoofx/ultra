// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a garbage collection allocation tick profile marker.
/// </summary>
public sealed record GCAllocationTickTraceMarker : UTraceMarker
{
    /// <summary>
    /// Gets or sets the amount of memory allocated.
    /// </summary>
    public required long AllocationAmount { get; set; }

    /// <summary>
    /// Gets or sets the kind of allocation.
    /// </summary>
    public required string? AllocationKind { get; set; }

    /// <summary>
    /// Gets or sets the type name of the allocated object.
    /// </summary>
    public required string? TypeName { get; set; }

    /// <summary>
    /// Gets or sets the index of the heap where the allocation occurred.
    /// </summary>
    public required int HeapIndex { get; set; }
}