// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a session of tracing.
/// </summary>
public sealed class UTraceSession
{
    /// <summary>
    /// Gets or sets the number of processor used for the session.
    /// </summary>
    public int NumberOfProcessors { get; set; }

    /// <summary>
    /// Gets or sets the CPU speed in MHz.
    /// </summary>
    public int CpuSpeedMHz { get; set; }

    /// <summary>
    /// Gets or sets the start time of the session.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Gets or sets the duration of the session.
    /// </summary>
    public UTimeSpan Duration { get; set; }

    /// <summary>
    /// Gets or sets the process being traced.
    /// </summary>
    public List<UTraceProcess> Processes { get; } = new();
}