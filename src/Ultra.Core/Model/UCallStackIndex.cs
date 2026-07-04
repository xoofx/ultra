// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a call stack index.
/// </summary>
public record struct UCallStackIndex(int Value)
{
    /// <summary>
    /// Gets the invalid call stack index.
    /// </summary>
    public static UCallStackIndex Invalid => new(-1);

    /// <summary>
    /// Gets a value indicating whether the index is valid.
    /// </summary>
    public bool IsValid => Value >= 0;

    /// <summary>
    /// Implicit conversion to <see cref="int"/>.
    /// </summary>
    public static implicit operator int(UCallStackIndex index) => index.Value;

    /// <summary>
    /// Explicit conversion from <see cref="int"/>.
    /// </summary>
    public static explicit operator UCallStackIndex(int value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}