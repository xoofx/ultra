// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;
using static Ultra.Core.FirefoxProfiler;

namespace Ultra.Core.Markers;

public class GCHeapStatsEvent : FirefoxProfiler.MarkerPayload
{
    public const string TypeId = "dotnet.gc.heap_stats";

    public GCHeapStatsEvent()
    {
        Type = TypeId;
    }

    public long TotalHeapSize { get; set; }

    public long TotalPromoted { get; set; }

    public long GenerationSize0 { get; set; }

    public long TotalPromotedSize0 { get; set; }

    public long GenerationSize1 { get; set; }

    public long TotalPromotedSize1 { get; set; }

    public long GenerationSize2 { get; set; }

    public long TotalPromotedSize2 { get; set; }

    public long GenerationSize3 { get; set; }

    public long TotalPromotedSize3 { get; set; }

    public long GenerationSize4 { get; set; }

    public long TotalPromotedSize4 { get; set; }

    public long FinalizationPromotedSize { get; set; }

    public long FinalizationPromotedCount { get; set; }

    public int PinnedObjectCount { get; set; }

    public int SinkBlockCount { get; set; }

    public int GCHandleCount { get; set; }
    
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
