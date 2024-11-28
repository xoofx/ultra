// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;
using static Ultra.Core.FirefoxProfiler;

namespace Ultra.Core.Markers;

/// <summary>
/// Represents a garbage collection heap stats event marker payload for Firefox Profiler.
/// </summary>
public class GCHeapStatsEvent : FirefoxProfiler.MarkerPayload
{
    /// <summary>
    /// The type identifier for the GCHeapStatsEvent.
    /// </summary>
    public const string TypeId = "dotnet.gc.heap_stats";

    /// <summary>
    /// Initializes a new instance of the <see cref="GCHeapStatsEvent"/> class.
    /// </summary>
    public GCHeapStatsEvent()
    {
        Type = TypeId;
    }

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

    /// <inheritdoc />
    protected internal override void WriteJson(Utf8JsonWriter writer, MarkerPayload payload, JsonSerializerOptions options)
    {
        writer.WriteNumber("totalHeapSize", TotalHeapSize);
        writer.WriteNumber("totalPromoted", TotalPromoted);
        writer.WriteNumber("generationSize0", GenerationSize0);
        writer.WriteNumber("totalPromotedSize0", TotalPromotedSize0);
        writer.WriteNumber("generationSize1", GenerationSize1);
        writer.WriteNumber("totalPromotedSize1", TotalPromotedSize1);
        writer.WriteNumber("generationSize2", GenerationSize2);
        writer.WriteNumber("totalPromotedSize2", TotalPromotedSize2);
        writer.WriteNumber("generationSize3", GenerationSize3);
        writer.WriteNumber("totalPromotedSize3", TotalPromotedSize3);
        writer.WriteNumber("generationSize4", GenerationSize4);
        writer.WriteNumber("totalPromotedSize4", TotalPromotedSize4);
        writer.WriteNumber("finalizationPromotedSize", FinalizationPromotedSize);
        writer.WriteNumber("finalizationPromotedCount", FinalizationPromotedCount);
        writer.WriteNumber("pinnedObjectCount", PinnedObjectCount);
        writer.WriteNumber("sinkBlockCount", SinkBlockCount);
        writer.WriteNumber("gcHandleCount", GCHandleCount);
    }

    /// <summary>
    /// Gets the schema for the GCHeapStatsEvent.
    /// </summary>
    /// <returns>The schema for the GCHeapStatsEvent.</returns>
    public static MarkerSchema Schema()
        => new()
        {
            Name = TypeId,
            ChartLabel = "GC Heap Stats: {marker.data.totalHeapSize}, Promoted: {marker.data.totalPromoted}",
            TableLabel = "GC Heap Stats: {marker.data.totalHeapSize}, Promoted: {marker.data.totalPromoted}",
            Display =
            {
                MarkerDisplayLocation.MarkerChart,
                MarkerDisplayLocation.MarkerTable,
                MarkerDisplayLocation.TimelineMemory,
            },
            Data =
            {
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "totalHeapSize",
                    Label = "Total Heap Size",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "totalPromoted",
                    Label = "Total Promoted",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "generationSize0",
                    Label = "Generation Size 0",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "totalPromotedSize0",
                    Label = "Total Promoted Size 0",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "generationSize1",
                    Label = "Generation Size 1",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "totalPromotedSize1",
                    Label = "Total Promoted Size 1",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "generationSize2",
                    Label = "Generation Size 2",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "totalPromotedSize2",
                    Label = "Total Promoted Size 2",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "generationSize3",
                    Label = "Generation Size 3",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "totalPromotedSize3",
                    Label = "Total Promoted Size 3",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "generationSize4",
                    Label = "Generation Size 4",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "totalPromotedSize4",
                    Label = "Total Promoted Size 4",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Bytes,
                    Key = "finalizationPromotedSize",
                    Label = "Finalization Promoted Size",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Integer,
                    Key = "finalizationPromotedCount",
                    Label = "Finalization Promoted Count",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Integer,
                    Key = "pinnedObjectCount",
                    Label = "Pinned Object Count",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Integer,
                    Key = "sinkBlockCount",
                    Label = "Sink Block Count",
                },
                new MarkerDataItem()
                {
                    Format = MarkerFormatType.Integer,
                    Key = "gcHandleCount",
                    Label = "GCHandle Count",
                },
            }
        };
}
