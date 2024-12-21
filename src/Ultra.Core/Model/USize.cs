// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a size in the traced process.
/// </summary>
public readonly record struct USize(ulong Value) : IComparable<USize>, IComparable
{
    /// <inheritdoc />
    public override string ToString() => $"0x{Value:X}";

    /// <summary>
    /// Implicit conversion to <see cref="ulong"/>.
    /// </summary>
    public static implicit operator ulong(USize size) => size.Value;

    /// <summary>
    /// Implicit conversion from <see cref="ulong"/>.
    /// </summary>
    public static implicit operator USize(ulong value) => new(value);

    /// <summary>
    /// Adds an offset to the size.
    /// </summary>
    public static USize operator +(USize size, long offset) => new(size.Value + (ulong)offset);

    /// <summary>
    /// Subtracts an offset from the size.
    /// </summary>
    public static USize operator -(USize size, long offset) => new(size.Value - (ulong)offset);

    /// <summary>
    /// Adds an offset to the size.
    /// </summary>
    public static USize operator +(USize size, ulong offset) => new(size.Value + offset);

    /// <summary>
    /// Subtracts an offset from the size.
    /// </summary>
    public static USize operator -(USize size, ulong offset) => new(size.Value - offset);

    /// <inheritdoc />
    public int CompareTo(USize other)
    {
        return Value.CompareTo(other.Value);
    }

    /// <inheritdoc />
    public int CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        return obj is USize other ? CompareTo(other) : throw new ArgumentException($"Object must be of type {nameof(USize)}");
    }

    /// <summary>
    /// Compares two sizes for less than.
    /// </summary>
    public static bool operator <(USize left, USize right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Compares two sizes for greater than.
    /// </summary>
    public static bool operator >(USize left, USize right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Compares two sizes for less than or equal.
    /// </summary>
    public static bool operator <=(USize left, USize right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Compares two sizes for greater than or equal.
    /// </summary>
    public static bool operator >=(USize left, USize right)
    {
        return left.CompareTo(right) >= 0;
    }
}