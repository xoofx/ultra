// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;

namespace Ultra.Core.Markers;

/// <summary>
/// Represents a garbage collection allocation tick event marker payload for Firefox Profiler.
/// </summary>
public class GCAllocationTickEvent : FirefoxProfiler.MarkerPayload
{
    /// <summary>
    /// The type identifier for GC allocation tick events.
    /// </summary>
    public const string TypeId = "GCMinor"; // dotnet.gc.allocation_tick

    /// <summary>
    /// Initializes a new instance of the <see cref="GCAllocationTickEvent"/> class.
    /// </summary>
    public GCAllocationTickEvent()
    {
        Type = TypeId;
        AllocationKind = string.Empty;
    }

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
    
    /// <inheritdoc />
    protected internal override void WriteJson(Utf8JsonWriter writer, FirefoxProfiler.MarkerPayload payload, JsonSerializerOptions options)
    {
        writer.WriteNumber("allocationAmount", AllocationAmount);
        writer.WriteString("allocationKind", AllocationKind);
        writer.WriteString("typeName", TypeName);
        writer.WriteNumber("heapIndex", HeapIndex);

        // Firefox Profiler expects nursery field, but it's ok as we don't have it
        // https://github.com/xoofx/firefox-profiler/blob/56ac64c17b79b964c1263e8022dd2db3399f230f/src/components/tooltip/GCMarker.js#L28-L32
    }

    /// <summary>
    /// Gets the schema for the GC allocation tick event.
    /// </summary>
    /// <returns>The marker schema.</returns>
    public static FirefoxProfiler.MarkerSchema Schema()
        => new()
        {
            Name = TypeId,
            ChartLabel = "GC Allocation: {marker.data.typeName}, Amount: {marker.data.allocationAmount}",
            TableLabel = "GC Allocation: {marker.data.typeName}, Amount: {marker.data.allocationAmount}",
            Display =
            {
                            FirefoxProfiler.MarkerDisplayLocation.TimelineOverview,
                            FirefoxProfiler.MarkerDisplayLocation.MarkerChart,
                            FirefoxProfiler.MarkerDisplayLocation.MarkerTable
            },
            Graphs = [
                new()
                            {
                                Key = "allocationAmount",
                                Color = FirefoxProfiler.ProfileColor.Red,
                                Type = FirefoxProfiler.MarkerGraphType.Bar,
                            }
            ],
            Data =
            {
                            new FirefoxProfiler.MarkerDataItem()
                            {
                                Format = FirefoxProfiler.MarkerFormatType.Bytes,
                                Key = "allocationAmount",
                                Label = "Allocation Amount",
                            },
                            new FirefoxProfiler.MarkerDataItem()
                            {
                                Format = FirefoxProfiler.MarkerFormatType.String,
                                Key = "allocationKind",
                                Label = "Allocation Kind",
                            },
                            new FirefoxProfiler.MarkerDataItem()
                            {
                                Format = FirefoxProfiler.MarkerFormatType.String,
                                Key = "typeName",
                                Label = "Type Name",
                            },
                            new FirefoxProfiler.MarkerDataItem()
                            {
                                Format = FirefoxProfiler.MarkerFormatType.Integer,
                                Key = "heapIndex",
                                Label = "Heap Index",
                            }
            }
        };
}
