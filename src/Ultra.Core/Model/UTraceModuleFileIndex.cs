// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a module file index.
/// </summary>
public record struct UTraceModuleFileIndex(int Value)
{
    /// <summary>
    /// Gets the invalid module file index.
    /// </summary>
    public static UTraceModuleFileIndex Invalid => new(-1);

    /// <summary>
    /// Gets a value indicating whether the index is valid.
    /// </summary>
    public bool IsValid => Value >= 0;

    /// <summary>
    /// Implicit conversion to <see cref="int"/>.
    /// </summary>
    public static implicit operator int(UTraceModuleFileIndex index) => index.Value;

    /// <summary>
    /// Explicit conversion from <see cref="int"/>.
    /// </summary>
    public static explicit operator UTraceModuleFileIndex(int value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}