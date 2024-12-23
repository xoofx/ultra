// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a profile marker.
/// </summary>
public abstract record UTraceMarker
{
    /// <summary>
    /// Gets or sets the start time of the marker. The time is relative to the start of the profiling session.
    /// </summary>
    public required UTimeSpan StartTime { get; set; }

    /// <summary>
    /// Gets or sets the duration of the marker.
    /// </summary>
    public UTimeSpan Duration { get; set; }
}