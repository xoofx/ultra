// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;

namespace Ultra.Core.Markers;

/// <summary>
/// The summary of a GC heap snapshot (<c>--heap-snapshot</c>), a marker on its <c>[heap]</c> track: what it holds and what was lost while
/// it was taken, so that the trace carries its own diagnosis (read by <c>ultra report</c>).
/// </summary>
public class HeapSnapshotEvent : FirefoxProfiler.MarkerPayload
{
    /// <summary>
    /// The type identifier for this event.
    /// </summary>
    public const string TypeId = "ultra.heap_snapshot";

    /// <summary>
    /// Initializes a new instance of the <see cref="HeapSnapshotEvent"/> class.
    /// </summary>
    public HeapSnapshotEvent()
    {
        Type = TypeId;
    }

    /// <summary>Gets or sets the live objects in the snapshot.</summary>
    public long Objects { get; set; }

    /// <summary>Gets or sets the bytes of the live objects.</summary>
    public long Bytes { get; set; }

    /// <summary>Gets or sets the events the runtime dropped while the snapshot was taken (its EventPipe buffer was full).</summary>
    public long EventsLost { get; set; }

    /// <summary>Gets or sets the object blocks (GCBulkNode events) missing: their objects are not in the snapshot.</summary>
    public long ObjectBlocksLost { get; set; }

    /// <summary>Gets or sets the reference blocks (GCBulkEdge events) missing: replaced by unknown references to keep the others in place.</summary>
    public long ReferenceBlocksLost { get; set; }

    /// <summary>Gets or sets the references announced by the objects that are unknown (lost, or replaced by placeholders).</summary>
    public long UnknownReferences { get; set; }

    /// <inheritdoc />
    protected internal override void WriteJson(Utf8JsonWriter writer, FirefoxProfiler.MarkerPayload payload, JsonSerializerOptions options)
    {
        writer.WriteNumber("objects", Objects);
        writer.WriteNumber("bytes", Bytes);
        writer.WriteNumber("eventsLost", EventsLost);
        writer.WriteNumber("objectBlocksLost", ObjectBlocksLost);
        writer.WriteNumber("referenceBlocksLost", ReferenceBlocksLost);
        writer.WriteNumber("unknownReferences", UnknownReferences);
    }

    /// <summary>
    /// Returns the schema for this marker.
    /// </summary>
    /// <returns>The schema for this marker.</returns>
    public static FirefoxProfiler.MarkerSchema Schema()
        => new()
        {
            Name = TypeId,
            ChartLabel = "GC heap snapshot: {marker.data.objects} objects, {marker.data.eventsLost} events lost",
            TableLabel = "GC heap snapshot: {marker.data.objects} objects, {marker.data.bytes} bytes, {marker.data.eventsLost} events lost",
            Display =
            {
                FirefoxProfiler.MarkerDisplayLocation.MarkerChart,
                FirefoxProfiler.MarkerDisplayLocation.MarkerTable
            },
            Data =
            {
                new FirefoxProfiler.MarkerDataItem { Format = FirefoxProfiler.MarkerFormatType.Integer, Key = "objects", Label = "Objects" },
                new FirefoxProfiler.MarkerDataItem { Format = FirefoxProfiler.MarkerFormatType.Bytes, Key = "bytes", Label = "Bytes" },
                new FirefoxProfiler.MarkerDataItem { Format = FirefoxProfiler.MarkerFormatType.Integer, Key = "eventsLost", Label = "Events lost" },
                new FirefoxProfiler.MarkerDataItem { Format = FirefoxProfiler.MarkerFormatType.Integer, Key = "objectBlocksLost", Label = "Object blocks lost" },
                new FirefoxProfiler.MarkerDataItem { Format = FirefoxProfiler.MarkerFormatType.Integer, Key = "referenceBlocksLost", Label = "Reference blocks lost" },
                new FirefoxProfiler.MarkerDataItem { Format = FirefoxProfiler.MarkerFormatType.Integer, Key = "unknownReferences", Label = "Unknown references" },
            },
        };
}
