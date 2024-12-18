// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;

namespace Ultra.Core.Markers;

/// <summary>
/// Represents a garbage collection event marker payload for Firefox Profiler.
/// </summary>
public class GCEvent : FirefoxProfiler.MarkerPayload
{
    /// <summary>
    /// The type identifier for the GC event.
    /// </summary>
    public const string TypeId = "GCMajor"; // Use a predefined type to have a different marker style in the timeline

    /// <summary>
    /// Initializes a new instance of the <see cref="GCEvent"/> class.
    /// </summary>
    public GCEvent()
    {
        Type = TypeId;
    }

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

    /// <inheritdoc />
    protected internal override void WriteJson(Utf8JsonWriter writer, FirefoxProfiler.MarkerPayload payload, JsonSerializerOptions options)
    {
        writer.WriteString("reason", Reason);
        writer.WriteNumber("count", Count);
        writer.WriteNumber("depth", Depth);
        writer.WriteString("gcType", GCType);

        // This is a dummy field to make it compatible with firefox-profiler - ugly hack, but there is no way to use our own marker styles, so we are reusing GCMajor here.
        // But we need to workaround the following code that expect some fields to be present:
        // https://github.com/xoofx/firefox-profiler/blob/56ac64c17b79b964c1263e8022dd2db3399f230f/src/components/tooltip/GCMarker.js#L218-L224
        writer.WriteStartObject("timings");
        writer.WriteString("status", string.Empty);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Returns the schema for the GC event marker.
    /// </summary>
    /// <returns>The schema for the GC event marker.</returns>
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
                }
            },
        };
}
