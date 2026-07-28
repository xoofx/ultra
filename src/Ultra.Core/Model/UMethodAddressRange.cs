// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;
using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// Represents the address range occupied by the native code of a method during a time interval.
/// </summary>
/// <remarks>
/// Unlike a module, a method address range has a lifetime: the code of a collectible method (e.g. a
/// <c>DynamicMethod</c>) is freed by the runtime and its code block is then recycled for another method.
/// A range is only meaningful between <see cref="BeginTime"/> (inclusive) and <see cref="EndTime"/> (exclusive).
/// </remarks>
/// <param name="BeginAddress">The first address of the code range.</param>
/// <param name="EndAddress">The address just past the last address of the code range.</param>
/// <param name="Index">The index of the method in <see cref="UTraceManagedMethodList"/>.</param>
/// <param name="BeginTime">The time at which the code was loaded at this address.</param>
/// <param name="EndTime">The time at which the code was unloaded, or <see cref="UTimeSpan.MaxValue"/> if it is still loaded.</param>
public readonly record struct UMethodAddressRange(UAddress BeginAddress, UAddress EndAddress, int Index, UTimeSpan BeginTime, UTimeSpan EndTime)
{
    /// <summary>
    /// Determines if the address is contained within the range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(ulong address) => address >= BeginAddress && address < EndAddress;

    /// <summary>
    /// Determines if the code was loaded at this address at the specified time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAliveAt(UTimeSpan time) => time >= BeginTime && time < EndTime;
}

/// <summary>
/// Represents a comparer for method address ranges to find the range containing an address.
/// </summary>
readonly record struct UMethodAddressRangeFinder(ulong Address) : IComparable<UMethodAddressRange>
{
    /// <summary>
    /// Compares the address with another method address range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(UMethodAddressRange other)
    {
        return other.Contains(Address) ? 0 : Address.CompareTo(other.BeginAddress);
    }
}

/// <summary>
/// Represents a comparer for method address ranges based on their beginning address and then on their beginning time.
/// </summary>
/// <remarks>
/// Sorting the ranges sharing the same address by time keeps the successive occupants of a recycled
/// code block in chronological order.
/// </remarks>
readonly record struct UMethodAddressRangeComparer : IComparerByRef<UMethodAddressRange>
{
    /// <summary>
    /// Compares two method address ranges for less than.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessThan(in UMethodAddressRange left, in UMethodAddressRange right)
        => left.BeginAddress == right.BeginAddress ? left.BeginTime < right.BeginTime : left.BeginAddress < right.BeginAddress;
}
