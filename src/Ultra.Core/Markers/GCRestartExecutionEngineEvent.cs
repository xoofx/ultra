// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Markers;

/// <summary>
/// Represents an event that indicates the .NET garbage collector has restarted the execution engine.
/// </summary>
public class GCRestartExecutionEngineEvent : FirefoxProfiler.MarkerPayload
{
    /// <summary>
    /// The type identifier for this event.
    /// </summary>
    public const string TypeId = "dotnet.gc.restart_execution_engine";

    /// <summary>
    /// Initializes a new instance of the <see cref="GCRestartExecutionEngineEvent"/> class.
    /// </summary>
    public GCRestartExecutionEngineEvent()
    {
        Type = TypeId;
    }

    /// <summary>
    /// Gets the schema for the GC Restart Execution Engine event.
    /// </summary>
    /// <returns>A <see cref="FirefoxProfiler.MarkerSchema"/> object that defines the schema.</returns>
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
