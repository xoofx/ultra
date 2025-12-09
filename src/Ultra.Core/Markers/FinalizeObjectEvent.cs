// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;

namespace Ultra.Core.Markers;

/// <summary>
/// Represents a garbage collection allocation tick event marker payload for Firefox Profiler.
/// </summary>
public class FinalizeObjectEvent : FirefoxProfiler.MarkerPayload
{
    /// <summary>
    /// The type identifier for the Contention event.
    /// </summary>
    public const string TypeId = "FinalizeObject"; // dotnet.gc.allocation_tick

    /// <summary>
    /// Initializes a new instance of the <see cref="GCAllocationTickEvent"/> class.
    /// </summary>
    public FinalizeObjectEvent()
    {
        Type = TypeId;
    }

    /// <summary>
    /// Gets or sets the owner of the lock object in bytes.
    /// </summary>
    public required string TypeName { get; set; }

    /// <inheritdoc />
    protected internal override void WriteJson(Utf8JsonWriter writer, FirefoxProfiler.MarkerPayload payload, JsonSerializerOptions options)
    {
        writer.WriteString("typeName", TypeName);
    }

    /// <summary>
    /// Gets the schema for the Contention event.
    /// </summary>
    /// <returns>The marker schema.</returns>
    public static FirefoxProfiler.MarkerSchema Schema()
        => new()
        {
            Name = TypeId,
            ChartLabel = "Finalize: {marker.data.typeName}",
            TableLabel = "Finalize: {marker.data.typeName}",
            Display =
            {
                            FirefoxProfiler.MarkerDisplayLocation.TimelineOverview,
                            FirefoxProfiler.MarkerDisplayLocation.MarkerChart,
                            FirefoxProfiler.MarkerDisplayLocation.MarkerTable
            },
            Graphs =        [ ],
            Data =
            {
                            new FirefoxProfiler.MarkerDataItem()
                            {
                                Format = FirefoxProfiler.MarkerFormatType.String,
                                Key = "typeName",
                                Label = "TypeName",
                            }
            }
        };
}
