// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;
using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// Represents an IL-to-native offset.
/// </summary>
public readonly record struct UNativeILOffset(int ILOffset, int NativeOffset);

/// <summary>
/// Represents a comparer for native IL offsets.
/// </summary>
readonly struct UNativeILOffsetComparer : IComparerByRef<UNativeILOffset>
{
    /// <summary>
    /// Compares two native IL offsets for less than.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessThan(in UNativeILOffset left, in UNativeILOffset right) => left.NativeOffset < right.NativeOffset;
}
