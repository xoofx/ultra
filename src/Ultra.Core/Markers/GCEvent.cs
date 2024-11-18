// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;

namespace Ultra.Core.Markers;

public class GCEvent : FirefoxProfiler.MarkerPayload
{
    public const string TypeId = "GCMajor"; // Use a predefined type to have a different marker style in the timeline

    public GCEvent()
    {
        Type = TypeId;
    }

    public string Reason { get; set; }

    public int Count { get; set; }

    public int Depth { get; set; }

    public string GCType { get; set; }
    
    protected internal override void WriteJson(Utf8JsonWriter writer, FirefoxProfiler.MarkerPayload payload, JsonSerializerOptions options)
    {
        writer.WriteString("reason", Reason);
        writer.WriteNumber("count", Count);
        writer.WriteNumber("depth", Depth);
        writer.WriteString("gcType", GCType);
    }

    public static FirefoxProfiler.MarkerSchema Schema()
        => new()
        {
            Name = TypeId,
            ChartLabel = "GC: {marker.data.reason}, Type: {marker.data.gcType}, Count: {marker.data.count}",
            TableLabel = "GC: {marker.data.reason}, Type: {marker.data.gcType}, Count: {marker.data.count}",
            Display =
            {
                FirefoxProfiler.MarkerDisplayLocation.TimelineMemory,
                FirefoxProfiler.MarkerDisplayLocation.MarkerChart,
                FirefoxProfiler.MarkerDisplayLocation.MarkerTable
            },
            Data =
            {
                new FirefoxProfiler.MarkerDataItem()
                {
                    Format = FirefoxProfiler.MarkerFormatType.String,
                    Key = "reason",
                    Label = "Reason",
                },
                new FirefoxProfiler.MarkerDataItem()
                {
                    Format = FirefoxProfiler.MarkerFormatType.Integer,
                    Key = "count",
                    Label = "Count",
                },
                new FirefoxProfiler.MarkerDataItem()
                {
                    Format = FirefoxProfiler.MarkerFormatType.Integer,
                    Key = "depth",
                    Label = "Depth",
                },
                new FirefoxProfiler.MarkerDataItem()
                {
                    Format = FirefoxProfiler.MarkerFormatType.String,
                    Key = "gcType",
                    Label = "GC Type",
                },
            },
        };
}