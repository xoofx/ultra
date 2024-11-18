// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Markers;

public class GCRestartExecutionEngineEvent : FirefoxProfiler.MarkerPayload
{
    public const string TypeId = "dotnet.gc.restart_execution_engine";

    public GCRestartExecutionEngineEvent()
    {
        Type = TypeId;
    }

    public static FirefoxProfiler.MarkerSchema Schema()
        => new()
        {
            Name = TypeId,
            ChartLabel = "GC Restart Execution Engine",
            TableLabel = "GC Restart Execution Engine",
            Display =
            {
                FirefoxProfiler.MarkerDisplayLocation.TimelineOverview,
                FirefoxProfiler.MarkerDisplayLocation.MarkerChart,
                FirefoxProfiler.MarkerDisplayLocation.MarkerTable
            }
        };
}