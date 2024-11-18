// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;

namespace Ultra.Core.Markers;

public class GCAllocationTickEvent : FirefoxProfiler.MarkerPayload
{
    public const string TypeId = "GCMinor"; // dotnet.gc.allocation_tick

    public GCAllocationTickEvent()
    {
        Type = TypeId;
    }

    public long AllocationAmount { get; set; }

    public string AllocationKind { get; set; }

    public string TypeName { get; set; }

    public int HeapIndex { get; set; }

    protected internal override void WriteJson(Utf8JsonWriter writer, FirefoxProfiler.MarkerPayload payload, JsonSerializerOptions options)
    {
        writer.WriteNumber("allocationAmount", AllocationAmount);
        writer.WriteString("allocationKind", AllocationKind);
        writer.WriteString("typeName", TypeName);
        writer.WriteNumber("heapIndex", HeapIndex);
    }

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
                },
            }
        };
}