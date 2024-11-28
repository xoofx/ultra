// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;

namespace Ultra.Core.Markers;

/// <summary>
/// Represents an event that marks the suspension of the execution engine by the garbage collector.
/// </summary>
public class GCSuspendExecutionEngineEvent : FirefoxProfiler.MarkerPayload
{
    /// <summary>
    /// The type identifier for this event.
    /// </summary>
    public const string TypeId = "dotnet.gc.suspend_execution_engine";

    /// <summary>
    /// Initializes a new instance of the <see cref="GCSuspendExecutionEngineEvent"/> class.
    /// </summary>
    public GCSuspendExecutionEngineEvent()
    {
        Type = TypeId;
    }

    /// <summary>
    /// Gets or sets the reason for the suspension.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets the count of suspensions.
    /// </summary>
    public int Count { get; set; }

    /// <inheritdoc />
    protected internal override void WriteJson(Utf8JsonWriter writer, FirefoxProfiler.MarkerPayload payload, JsonSerializerOptions options)
    {
        writer.WriteString("reason", Reason);
        writer.WriteNumber("count", Count);
    }

    /// <summary>
    /// Returns the schema for this marker.
    /// </summary>
    /// <returns>The schema for this marker.</returns>
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
