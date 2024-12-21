// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a garbage collection allocation tick profile marker.
/// </summary>
public record GCAllocationTickTraceMarker : UTraceMarker
{
    /// <summary>
    /// Gets or sets the amount of memory allocated.
    /// </summary>
    public long AllocationAmount { get; set; }

    /// <summary>
    /// Gets or sets the kind of allocation.
    /// </summary>
    public string? AllocationKind { get; set; }

    /// <summary>
    /// Gets or sets the type name of the allocated object.
    /// </summary>
    public string? TypeName { get; set; }

    /// <summary>
    /// Gets or sets the index of the heap where the allocation occurred.
    /// </summary>
    public int HeapIndex { get; set; }
}