// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// Represents a list of <see cref="UTraceManagedMethod"/> instances.
/// </summary>
/// <remarks>
/// The code of a collectible method (e.g. a <c>DynamicMethod</c>) is freed by the runtime and the
/// address of its code block is then recycled for another method. Code ranges are therefore recorded
/// with the time interval during which they were loaded, and looking up a method by address requires
/// the time of the sample.
/// </remarks>
public sealed class UTraceManagedMethodList : UGenericList<UTraceManagedMethod>
{
    private UnsafeDictionary<UAddress, int> _mapMethodAddressToRangeIndex = new();
    private UnsafeDictionary<long, int> _mapManagedMethodIDToMethodIndex = new();
    private UnsafeList<UMethodAddressRange> _methodAddressRanges = new();

    /// <summary>
    /// Gets or creates a managed method based on the provided details.
    /// </summary>
    /// <param name="threadID">The thread ID.</param>
    /// <param name="moduleID">The module ID.</param>
    /// <param name="methodID">The method ID.</param>
    /// <param name="methodNamespace">The namespace of the method.</param>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="methodSignature">The signature of the method.</param>
    /// <param name="methodToken">The method token.</param>
    /// <param name="methodFlags">The method flags.</param>
    /// <param name="methodStartAddress">The starting address of the method.</param>
    /// <param name="methodSize">The size of the method.</param>
    /// <param name="loadTime">The time at which the code was loaded at <paramref name="methodStartAddress"/>. Use <c>default</c> for a rundown event, for which the actual load time is unknown.</param>
    /// <returns>The <see cref="UTraceManagedMethod"/> instance.</returns>
    public UTraceManagedMethod GetOrCreateManagedMethod(int threadID, long moduleID, long methodID, string methodNamespace, string methodName, string methodSignature, int methodToken, MethodFlags methodFlags, UAddress methodStartAddress, USize methodSize, UTimeSpan loadTime)
    {
        if (_mapManagedMethodIDToMethodIndex.TryGetValue(methodID, out var methodIndex))
        {
            // A method can be recompiled at a different address (e.g. tiered compilation),
            // so record the additional code range for the same method.
            AddCodeRange(methodStartAddress, methodSize, methodIndex, loadTime);
            return List[methodIndex];
        }
        var method = new UTraceManagedMethod(threadID, moduleID, methodID, methodNamespace, methodName, methodSignature, methodToken, methodFlags, methodStartAddress, methodSize);
        methodIndex = List.Count;
        _mapManagedMethodIDToMethodIndex.Add(methodID, methodIndex);
        List.Add(method);
        AddCodeRange(methodStartAddress, methodSize, methodIndex, loadTime);
        return method;
    }

    /// <summary>
    /// Records that the code of a method has been unloaded, closing the code range starting at the specified address.
    /// </summary>
    /// <param name="methodID">The method ID.</param>
    /// <param name="methodStartAddress">The starting address of the method.</param>
    /// <param name="unloadTime">The time at which the code was unloaded.</param>
    /// <remarks>
    /// Nothing is done if the method is unknown or if the code block at this address has already been
    /// taken over by another method.
    /// </remarks>
    public void UnloadManagedMethod(long methodID, UAddress methodStartAddress, UTimeSpan unloadTime)
    {
        if (!_mapManagedMethodIDToMethodIndex.TryGetValue(methodID, out var methodIndex)) return;
        if (!_mapMethodAddressToRangeIndex.TryGetValue(methodStartAddress, out var rangeIndex)) return;

        ref var range = ref _methodAddressRanges.UnsafeGetRefAt(rangeIndex);
        if (range.Index != methodIndex) return;
        CloseCodeRange(ref range, unloadTime);
    }

    /// <summary>
    /// Sorts the method address ranges.
    /// </summary>
    public void SortMethodAddressRanges()
    {
        _methodAddressRanges.AsSpan().SortByRef(new UMethodAddressRangeComparer());
    }

    /// <summary>
    /// Tries to find a method by its ID.
    /// </summary>
    /// <param name="methodID">The method ID.</param>
    /// <param name="method">The method found, if any.</param>
    /// <returns>True if the method was found, otherwise false.</returns>
    public bool TryFindMethodById(long methodID, [NotNullWhen(true)] out UTraceManagedMethod? method)
    {
        if (_mapManagedMethodIDToMethodIndex.TryGetValue(methodID, out var methodIndex))
        {
            method = List[methodIndex];
            return true;
        }
        method = null;
        return false;
    }

    /// <summary>
    /// Tries to find the method loaded at the specified address at the specified time.
    /// </summary>
    /// <param name="address">The address to resolve.</param>
    /// <param name="time">The time at which the address was captured.</param>
    /// <param name="method">The method found, if any.</param>
    /// <returns>True if the method was found, otherwise false.</returns>
    /// <remarks><see cref="SortMethodAddressRanges"/> must have been called before using this method.</remarks>
    public bool TryFindMethodByAddress(UAddress address, UTimeSpan time, [NotNullWhen(true)] out UTraceManagedMethod? method)
    {
        if (TryFindMethodIndexByAddress(address, time, out var methodIndex))
        {
            method = List[methodIndex];
            return true;
        }
        method = null;
        return false;
    }

    /// <summary>
    /// Tries to find the index of the method loaded at the specified address at the specified time.
    /// </summary>
    /// <param name="address">The address to resolve.</param>
    /// <param name="time">The time at which the address was captured.</param>
    /// <param name="methodIndex">The index of the method found in this list, or -1 if not found.</param>
    /// <returns>True if the method was found, otherwise false.</returns>
    /// <remarks><see cref="SortMethodAddressRanges"/> must have been called before using this method.</remarks>
    public bool TryFindMethodIndexByAddress(UAddress address, UTimeSpan time, out int methodIndex)
    {
        var ranges = _methodAddressRanges.AsSpan();
        var index = ranges.BinarySearch(new UMethodAddressRangeFinder(address));
        if (index < 0)
        {
            methodIndex = -1;
            return false;
        }

        // Several methods can occupy the same address over the lifetime of a process (the code block of a
        // collected dynamic method is recycled), and the binary search lands on an arbitrary one of them,
        // so the neighbouring ranges are scanned to select the method that was loaded at that time.
        var first = index;
        while (first > 0 && ranges[first - 1].Contains(address)) first--;
        var last = index;
        while (last + 1 < ranges.Length && ranges[last + 1].Contains(address)) last++;

        var found = -1;
        for (var i = first; i <= last; i++)
        {
            // Ranges are sorted by time for a same address, so the last match is the most recent one.
            if (ranges[i].IsAliveAt(time)) found = i;
        }

        // No range covering that time (e.g. a method only known through a rundown event, or a sample
        // taken while the code block was free): fallback to the most recent occupant of the address.
        methodIndex = ranges[found >= 0 ? found : last].Index;
        return true;
    }

    private void AddCodeRange(UAddress startAddress, USize size, int methodIndex, UTimeSpan loadTime)
    {
        var endAddress = startAddress + size;
        if (_mapMethodAddressToRangeIndex.TryGetValue(startAddress, out var rangeIndex))
        {
            ref var previousRange = ref _methodAddressRanges.UnsafeGetRefAt(rangeIndex);
            if (previousRange.Index == methodIndex)
            {
                // The same method was already reported at this address (e.g. a load event followed by a
                // rundown event): only make sure the range is large enough to cover both reports.
                if (endAddress > previousRange.EndAddress)
                {
                    previousRange = previousRange with { EndAddress = endAddress };
                }
                return;
            }

            // Another method is reusing the code block of a collected method. The unload event is not
            // always available (it can be missing from the trace), so close the previous range here too.
            CloseCodeRange(ref previousRange, loadTime);
        }

        _mapMethodAddressToRangeIndex[startAddress] = _methodAddressRanges.Count;
        _methodAddressRanges.Add(new(startAddress, endAddress, methodIndex, loadTime, UTimeSpan.MaxValue));
    }

    private static void CloseCodeRange(ref UMethodAddressRange range, UTimeSpan endTime)
    {
        if (range.EndTime != UTimeSpan.MaxValue) return;
        // Never let a range end before it starts (an unload can be reported with the same timestamp as
        // the load, and a rundown event carries no meaningful load time).
        range = range with { EndTime = endTime > range.BeginTime ? endTime : range.BeginTime };
    }
}
