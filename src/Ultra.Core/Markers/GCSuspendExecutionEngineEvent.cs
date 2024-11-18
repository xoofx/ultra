// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;

namespace Ultra.Core.Markers;

public class GCSuspendExecutionEngineEvent : FirefoxProfiler.MarkerPayload
{
    public const string TypeId = "dotnet.gc.suspend_execution_engine";

    public GCSuspendExecutionEngineEvent()
    {
        Type = TypeId;
    }

    public string Reason { get; set; }

    public int Count { get; set; }
    
    protected internal override void WriteJson(Utf8JsonWriter writer, FirefoxProfiler.MarkerPayload payload, JsonSerializerOptions options)
    {
        writer.WriteString("reason", Reason);
        writer.WriteNumber("count", Count);
    }

    public static FirefoxProfiler.MarkerSchema Schema()
        => new()
        {
            Name = TypeId,
            ChartLabel = "GC Suspend Execution Engine: {marker.data.reason}, Count: {marker.data.count}",
            TableLabel = "GC Suspend Execution Engine: {marker.data.reason}, Count: {marker.data.count}",
            Display =
            {
                FirefoxProfiler.MarkerDisplayLocation.TimelineOverview,
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
            },
        };
}