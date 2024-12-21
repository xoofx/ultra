// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a JIT compile event marker payload for the Firefox Profiler.
/// </summary>
public record JitCompileTraceMarker : UTraceMarker
{
    /// <summary>
    /// Gets or sets the full name of the method.
    /// </summary>
    public required string FullName { get; init; }

    /// <summary>
    /// Gets or sets the IL size of the method.
    /// </summary>
    public int MethodILSize { get; set; }
}