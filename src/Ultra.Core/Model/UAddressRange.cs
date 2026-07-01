// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;
using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// Represents an address range in the traced process.
/// </summary>
public readonly record struct UAddressRange(UAddress BeginAddress, UAddress EndAddress, int Index)
{
    /// <summary>
    /// Determines if the address is contained within the range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(ulong address) => address >= BeginAddress && address < EndAddress;
}

/// <summary>
/// Represents a comparer for address ranges.
/// </summary>
readonly record struct UAddressRangeFinder(ulong Address) : IComparable<UAddressRange>
{
    /// <summary>
    /// Compares the address range finder with another address range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(UAddressRange other)
    {
        return other.Contains(Address) ? 0 : Address.CompareTo(other.BeginAddress);
    }
}

/// <summary>
/// Represents a comparer for address ranges based on their beginning address.
/// </summary>
readonly record struct UAddressRangeComparer : IComparerByRef<UAddressRange>
{
    /// <summary>
    /// Compares two address ranges for less than.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessThan(in UAddressRange left, in UAddressRange right) => left.BeginAddress < right.BeginAddress;
}
