// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a JIT compile event marker payload for the Firefox Profiler.
/// </summary>
public sealed record JitCompileTraceMarker : UTraceMarker
{
    /// <summary>
    /// Gets or sets the full name of the method.
    /// </summary>
    public required string FullName { get; init; }

    /// <summary>
    /// Gets or sets the namespace of the method.
    /// </summary>
    public required string MethodNamespace { get; init; }

    /// <summary>
    /// Gets or sets the name of the method.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// Gets or sets the signature of the method.
    /// </summary>
    public required string MethodSignature { get; init; }

    /// <summary>
    /// Gets or sets the IL size of the method.
    /// </summary>
    public required int MethodILSize { get; set; }
}