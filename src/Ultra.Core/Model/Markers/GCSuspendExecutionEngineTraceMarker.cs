// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents an event that marks the suspension of the execution engine by the garbage collector.
/// </summary>
public sealed record GCSuspendExecutionEngineTraceMarker : UTraceMarker
{
    /// <summary>
    /// Gets or sets the reason for the suspension.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets the count of suspensions.
    /// </summary>
    public int Count { get; set; }
}