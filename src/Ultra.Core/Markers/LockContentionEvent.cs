// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Globalization;
using System.Text.Json;

namespace Ultra.Core.Markers;

/// <summary>
/// A wait of a thread for a lock held by another thread (<c>--locks</c>: the CLR ContentionStart/ContentionStop events), a marker on the
/// waiting thread from the start to the end of the wait.
/// </summary>
public class LockContentionEvent : FirefoxProfiler.MarkerPayload
{
    /// <summary>
    /// The type identifier for this event.
    /// </summary>
    public const string TypeId = "dotnet.lock_contention";

    /// <summary>
    /// The name of the markers.
    /// </summary>
    public const string MarkerName = "Lock Contention";

    /// <summary>
    /// Initializes a new instance of the <see cref="LockContentionEvent"/> class.
    /// </summary>
    public LockContentionEvent()
    {
        Type = TypeId;
    }

    /// <summary>Gets or sets how long the thread waited, in ms (the runtime's own measure when it gives one).</summary>
    public double DurationMs { get; set; }

    /// <summary>Gets or sets the kind of lock: Managed (a .NET lock) or Native (a lock of the runtime).</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the id of the lock (0 when the runtime does not give it, before .NET 8).</summary>
    public ulong LockId { get; set; }

    /// <summary>Gets or sets the address of the object locked (0 when not given).</summary>
    public ulong ObjectId { get; set; }

    /// <summary>Gets or sets the OS thread id of the thread that held the lock when the wait started (0 when not given).</summary>
    public long OwnerThreadId { get; set; }

    /// <summary>Gets or sets the stack where the thread waited, an index into the stack table of its thread (null when not recorded).</summary>
    public int? CauseStack { get; set; }

    /// <summary>The ETW call stack of the wait, resolved to <see cref="CauseStack"/> when the thread is converted (-1: none).</summary>
    internal int PendingCallStack { get; set; } = -1;

    /// <inheritdoc />
    protected internal override void WriteJson(Utf8JsonWriter writer, FirefoxProfiler.MarkerPayload payload, JsonSerializerOptions options)
    {
        writer.WriteNumber("durationMs", Math.Round(DurationMs, 6));
        writer.WriteString("kind", Kind);
        // As hexadecimal strings: a 64-bit address does not fit the doubles of JavaScript
        writer.WriteString("lockId", LockId == 0 ? "" : $"0x{LockId.ToString("x", CultureInfo.InvariantCulture)}");
        writer.WriteString("objectId", ObjectId == 0 ? "" : $"0x{ObjectId.ToString("x", CultureInfo.InvariantCulture)}");
        writer.WriteNumber("ownerThreadId", OwnerThreadId);
        if (CauseStack is { } stack)
        {
            // The Firefox Profiler shows the stack of a marker from its cause
            writer.WriteStartObject("cause");
            writer.WriteNumber("stack", stack);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Returns the schema for this marker.
    /// </summary>
    /// <returns>The schema for this marker.</returns>
    public static FirefoxProfiler.MarkerSchema Schema()
        => new()
        {
            Name = TypeId,
            ChartLabel = "Lock contention: {marker.data.durationMs} ms, owner {marker.data.ownerThreadId}",
            TooltipLabel = "Lock contention: {marker.data.durationMs} ms",
            TableLabel = "Lock contention: {marker.data.durationMs} ms, owner thread {marker.data.ownerThreadId}, lock {marker.data.lockId}",
            Display =
            {
                FirefoxProfiler.MarkerDisplayLocation.TimelineOverview,
                FirefoxProfiler.MarkerDisplayLocation.MarkerChart,
                FirefoxProfiler.MarkerDisplayLocation.MarkerTable
            },
            Data =
            {
                new FirefoxProfiler.MarkerDataItem { Format = FirefoxProfiler.MarkerFormatType.Milliseconds, Key = "durationMs", Label = "Wait" },
                new FirefoxProfiler.MarkerDataItem { Format = FirefoxProfiler.MarkerFormatType.String, Key = "kind", Label = "Kind" },
                new FirefoxProfiler.MarkerDataItem { Format = FirefoxProfiler.MarkerFormatType.Tid, Key = "ownerThreadId", Label = "Owner thread" },
                new FirefoxProfiler.MarkerDataItem { Format = FirefoxProfiler.MarkerFormatType.String, Key = "lockId", Label = "Lock" },
                new FirefoxProfiler.MarkerDataItem { Format = FirefoxProfiler.MarkerFormatType.String, Key = "objectId", Label = "Object" },
            },
        };
}
