// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// A wait of a thread for a lock held by another thread (<c>--locks</c>: the CLR ContentionStart/ContentionStop events).
/// </summary>
public sealed record LockContentionTraceMarker : UTraceMarker
{
    /// <summary>
    /// Gets or sets the kind of lock: Managed or Native.
    /// </summary>
    public required string? Kind { get; set; }

    /// <summary>
    /// Gets or sets the id of the lock (0 when not given).
    /// </summary>
    public required ulong LockId { get; set; }

    /// <summary>
    /// Gets or sets the address of the object locked (0 when not given).
    /// </summary>
    public required ulong ObjectId { get; set; }

    /// <summary>
    /// Gets or sets the OS thread id of the lock owner when the wait started (0 when not given).
    /// </summary>
    public required long OwnerThreadId { get; set; }

    /// <summary>
    /// Gets or sets how long the thread waited, in ms, as measured by the runtime (0 when not given: then <see cref="UTraceMarker.Duration"/>).
    /// </summary>
    public double DurationMs { get; set; }
}
