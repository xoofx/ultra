// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents an address in the traced process.
/// </summary>
public readonly record struct UAddress(ulong Value) : IComparable<UAddress>, IComparable
{
    /// <inheritdoc />
    public override string ToString() => $"0x{Value:X}";

    /// <summary>
    /// Adds an offset to the address.
    /// </summary>
    public static UAddress operator +(UAddress address, ulong offset) => new(address.Value + offset);

    /// <summary>
    /// Subtracts an offset from the address.
    /// </summary>
    public static UAddress operator -(UAddress address, ulong offset) => new(address.Value - offset);

    /// <summary>
    /// Adds an offset to the address.
    /// </summary>
    public static UAddress operator +(UAddress address, long offset) => new(address.Value + (ulong)offset);

    /// <summary>
    /// Subtracts an offset from the address.
    /// </summary>
    public static UAddress operator -(UAddress address, long offset) => new(address.Value - (ulong)offset);

    /// <summary>
    /// Adds an offset to the address.
    /// </summary>
    public static UAddress operator +(UAddress address, int offset) => new(address.Value + (ulong)offset);

    /// <summary>
    /// Subtracts an offset from the address.
    /// </summary>
    public static UAddress operator -(UAddress address, int offset) => new(address.Value - (ulong)offset);

    /// <summary>
    /// Subtracts two addresses to return the size difference.
    /// </summary>
    public static USize operator -(UAddress left, UAddress right) => new(left.Value - right.Value);

    /// <summary>
    /// Implicit conversion to <see cref="ulong"/>.
    /// </summary>
    public static implicit operator ulong(UAddress address) => address.Value;

    /// <summary>
    /// Explicit conversion from <see cref="ulong"/>.
    /// </summary>
    public static implicit operator UAddress(ulong value) => new(value);

    /// <inheritdoc />
    public int CompareTo(UAddress other)
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

        return obj is UAddress other ? CompareTo(other) : throw new ArgumentException($"Object must be of type {nameof(UAddress)}");
    }

    /// <summary>
    /// Compares two addresses for less than.
    /// </summary>
    public static bool operator <(UAddress left, UAddress right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Compares two addresses for greater than.
    /// </summary>
    public static bool operator >(UAddress left, UAddress right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Compares two addresses for less than or equal.
    /// </summary>
    public static bool operator <=(UAddress left, UAddress right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Compares two addresses for greater than or equal.
    /// </summary>
    public static bool operator >=(UAddress left, UAddress right)
    {
        return left.CompareTo(right) >= 0;
    }
}