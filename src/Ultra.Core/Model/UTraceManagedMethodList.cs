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
/// <para>
/// The runtime recycles the id of such a method too (the method descriptor of a collected <c>DynamicMethod</c> is
/// given to the next one), so an id alone does not tell a method: an event with a known id and another name,
/// signature, token or module is another method.
/// </para>
/// </remarks>
public sealed class UTraceManagedMethodList : UGenericList<UTraceManagedMethod>
{
    private UnsafeDictionary<UAddress, int> _mapMethodAddressToRangeIndex = new();   // the last range added at a start address
    private UnsafeDictionary<long, int> _mapManagedMethodIDToMethodIndex = new();    // the last method that had an id
    private UnsafeList<UMethodAddressRange> _methodAddressRanges = new();
    // Per range once sorted: the furthest end of the ranges up to it. No range before it holds an address at or past it, so a
    // lookup knows where to stop looking back (the ranges of a recycled memory overlap, at one start or at another).
    private ulong[] _reach = [];
    // False once a range was added or grew since the last sort: the order and _reach are stale, a lookup sorts first
    private bool _isSorted = true;

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
    /// <param name="tier">The optimization tier of this code version (Tier0, Tier1, OSR...), unknown when the event has none.</param>
    /// <param name="rundownTime">For a rundown event (<paramref name="loadTime"/> is <c>default</c>): the time of the event, at which the code was there. When
    /// the address had another method before, that one ended at the latest then and this one was there since.</param>
    /// <returns>The <see cref="UTraceManagedMethod"/> instance.</returns>
    public UTraceManagedMethod GetOrCreateManagedMethod(int threadID, long moduleID, long methodID, string methodNamespace, string methodName, string methodSignature, int methodToken, MethodFlags methodFlags, UAddress methodStartAddress, USize methodSize, UTimeSpan loadTime, OptimizationTier tier = OptimizationTier.Unknown, UTimeSpan rundownTime = default)
    {
        // A method can be recompiled at a different address (e.g. tiered compilation): an additional code range for the same method.
        var methodIndex = GetOrCreateMethodIndex(threadID, moduleID, methodID, methodNamespace, methodName, methodSignature, methodToken, methodFlags, methodStartAddress, methodSize);
        AddCodeRange(methodStartAddress, methodSize, methodIndex, loadTime, rundownTime, tier);
        return List[methodIndex];
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
    /// Records that the code of a method has been unloaded, from its unload event: the code range starting at the address is
    /// closed. A method that was never reported as loaded (it was there before the events were enabled) is recorded as there
    /// until its unload, so that the samples taken before it stay symbolized.
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
    /// <param name="unloadTime">The time at which the code was unloaded.</param>
    /// <param name="tier">The optimization tier of this code version, unknown when the event has none.</param>
    /// <remarks>
    /// Nothing is done if the code block at this address has been taken over by another method that is still there.
    /// </remarks>
    public void UnloadManagedMethod(int threadID, long moduleID, long methodID, string methodNamespace, string methodName, string methodSignature, int methodToken, MethodFlags methodFlags, UAddress methodStartAddress, USize methodSize, UTimeSpan unloadTime, OptimizationTier tier = OptimizationTier.Unknown)
    {
        var methodIndex = GetOrCreateMethodIndex(threadID, moduleID, methodID, methodNamespace, methodName, methodSignature, methodToken, methodFlags, methodStartAddress, methodSize);
        var endAddress = methodStartAddress + methodSize;
        var beginTime = default(UTimeSpan);
        if (_mapMethodAddressToRangeIndex.TryGetValue(methodStartAddress, out var rangeIndex))
        {
            ref var range = ref _methodAddressRanges.UnsafeGetRefAt(rangeIndex);
            if (range.EndTime == UTimeSpan.MaxValue)
            {
                if (range.Index != methodIndex) return;
                Merge(ref range, endAddress, tier);
                CloseCodeRange(ref range, unloadTime);
                return;
            }

            // The range at this address ended before: this method came after it, without a load event. Unless it is the same
            // unload told twice
            if (range.EndTime >= unloadTime) return;
            beginTime = range.EndTime;
        }

        AddRange(new(methodStartAddress, endAddress, methodIndex, beginTime, unloadTime, tier));
    }

    /// <summary>
    /// Sorts the method address ranges.
    /// </summary>
    public void SortMethodAddressRanges()
    {
        var ranges = _methodAddressRanges.AsSpan();
        ranges.SortByRef(new UMethodAddressRangeComparer());

        var reach = new ulong[ranges.Length];
        ulong furthest = 0;
        for (var i = 0; i < ranges.Length; i++)
        {
            furthest = Math.Max(furthest, ranges[i].EndAddress);
            reach[i] = furthest;
            // the ranges moved: the last one of a start address (they are in time order) is the one a later event of that address meets
            _mapMethodAddressToRangeIndex[ranges[i].BeginAddress] = i;
        }

        _reach = reach;
        _isSorted = true;
    }

    /// <summary>
    /// Tries to find a method by its ID.
    /// </summary>
    /// <param name="methodID">The method ID.</param>
    /// <param name="method">The method found, if any.</param>
    /// <returns>True if the method was found, otherwise false.</returns>
    /// <remarks>The id of an unloaded method is given to a later one: this is the last method that had the id.</remarks>
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
    public bool TryFindMethodIndexByAddress(UAddress address, UTimeSpan time, out int methodIndex)
    {
        if (TryFindCodeRangeByAddress(address, time, out var range))
        {
            methodIndex = range.Index;
            return true;
        }
        methodIndex = -1;
        return false;
    }

    /// <summary>
    /// Tries to find the code range - the method (<see cref="UMethodAddressRange.Index"/>), the start of its code and its
    /// optimization tier - that was loaded at the specified address at the specified time.
    /// </summary>
    /// <param name="address">The address to resolve.</param>
    /// <param name="time">The time at which the address was captured.</param>
    /// <param name="range">The code range found, if any.</param>
    /// <returns>True if a code range was found, otherwise false.</returns>
    /// <remarks>
    /// The code version of a sampled frame is this range: a method that comes back to its address after its unload (same id,
    /// same name, another compilation) has two ranges there, and only the time of the sample tells which one the frame was in.
    /// </remarks>
    public bool TryFindCodeRangeByAddress(UAddress address, UTimeSpan time, out UMethodAddressRange range)
    {
        if (!_isSorted) SortMethodAddressRanges();
        var ranges = _methodAddressRanges.AsSpan();

        // Several methods can hold the same address over the lifetime of a process (the code block of a collected dynamic
        // method is recycled, at its start or inside it): all the ranges with the address are looked at, from the last one
        // starting at or before it back, for the one that was loaded at that time - the most recent one first.
        var found = -1;
        long foundDistance = long.MaxValue;
        for (var i = LastRangeStartingAtOrBefore(ranges, address); i >= 0 && _reach[i] > address; i--)
        {
            ref readonly var candidate = ref ranges[i];
            if (!candidate.Contains(address)) continue;
            if (candidate.IsAliveAt(time))
            {
                found = i;
                break;
            }

            // No range covering that time (e.g. a sample taken while the code block was free, or just before the load event of
            // its method): the one nearest in time
            var distance = time < candidate.BeginTime ? candidate.BeginTime.Value.Ticks - time.Value.Ticks : time.Value.Ticks - candidate.EndTime.Value.Ticks;
            if (distance < foundDistance)
            {
                found = i;
                foundDistance = distance;
            }
        }

        if (found < 0)
        {
            range = default;
            return false;
        }

        range = ranges[found];
        return true;
    }

    /// <summary>
    /// The start of the code range of method <paramref name="methodIndex"/> that holds <paramref name="address"/> (a method has one per
    /// compilation: tier 0, tier 1, OSR): to express a frame as an offset in its function's code.
    /// </summary>
    public bool TryFindMethodCodeStart(UAddress address, int methodIndex, out UAddress codeStart) => TryFindMethodCodeStart(address, methodIndex, out codeStart, out _);

    /// <summary>
    /// The start and the optimization tier of the code version (code range) of method <paramref name="methodIndex"/> that holds
    /// <paramref name="address"/>: to express a frame as an offset in the code of its version, and to name the version.
    /// </summary>
    /// <remarks>
    /// Without the time of a sample: when the method had the address in more than one range (it came back to it after its
    /// unload), this is the most recent one. The version of a sampled frame comes from <see cref="TryFindCodeRangeByAddress"/>,
    /// which <see cref="UCodeAddressList"/> keeps with the frame's code address.
    /// </remarks>
    public bool TryFindMethodCodeStart(UAddress address, int methodIndex, out UAddress codeStart, out OptimizationTier tier)
    {
        if (!_isSorted) SortMethodAddressRanges();
        var ranges = _methodAddressRanges.AsSpan();
        // the most recent range of that method with the address (the ranges of one start are in time order)
        for (var i = LastRangeStartingAtOrBefore(ranges, address); i >= 0 && _reach[i] > address; i--)
        {
            if (ranges[i].Index == methodIndex && ranges[i].Contains(address))
            {
                codeStart = ranges[i].BeginAddress;
                tier = ranges[i].Tier;
                return true;
            }
        }
        codeStart = default;
        tier = OptimizationTier.Unknown;
        return false;
    }

    private static int LastRangeStartingAtOrBefore(ReadOnlySpan<UMethodAddressRange> ranges, UAddress address)
    {
        var low = 0;
        var high = ranges.Length;
        while (low < high)
        {
            var middle = (int)(((uint)low + (uint)high) >> 1);
            if (ranges[middle].BeginAddress <= address) low = middle + 1;
            else high = middle;
        }
        return low - 1;
    }

    private int GetOrCreateMethodIndex(int threadID, long moduleID, long methodID, string methodNamespace, string methodName, string methodSignature, int methodToken, MethodFlags methodFlags, UAddress methodStartAddress, USize methodSize)
    {
        if (_mapManagedMethodIDToMethodIndex.TryGetValue(methodID, out var methodIndex))
        {
            var known = List[methodIndex];
            if (known.ModuleID == moduleID && known.MethodToken == methodToken && known.MethodName == methodName && known.MethodNamespace == methodNamespace && known.MethodSignature == methodSignature)
            {
                return methodIndex;
            }
            // The id of an unloaded method given to another one (see the remarks of the class): the id is this method's from now on
        }

        methodIndex = List.Count;
        List.Add(new UTraceManagedMethod(threadID, moduleID, methodID, methodNamespace, methodName, methodSignature, methodToken, methodFlags, methodStartAddress, methodSize));
        _mapManagedMethodIDToMethodIndex[methodID] = methodIndex;
        return methodIndex;
    }

    private void AddCodeRange(UAddress startAddress, USize size, int methodIndex, UTimeSpan loadTime, UTimeSpan rundownTime, OptimizationTier tier)
    {
        var endAddress = startAddress + size;
        var isLoad = loadTime != default;
        var beginTime = loadTime;
        if (_mapMethodAddressToRangeIndex.TryGetValue(startAddress, out var rangeIndex))
        {
            ref var previousRange = ref _methodAddressRanges.UnsafeGetRefAt(rangeIndex);
            var previousIsOpen = previousRange.EndTime == UTimeSpan.MaxValue;
            if (previousRange.Index == methodIndex && (previousIsOpen || (!isLoad && rundownTime == default)))
            {
                // The same method was already reported at this address (e.g. a load event followed by a
                // rundown event): only make sure the range is large enough to cover both reports, and keep
                // a known tier.
                Merge(ref previousRange, endAddress, tier);
                return;
            }

            // Another method is reusing the code block of a collected method - or the same one came back to it after its
            // unload. The unload event is not always available (it can be missing from the trace), so close the previous
            // range here too: at this load, or for a method only a rundown names at the latest at the rundown.
            CloseCodeRange(ref previousRange, isLoad ? loadTime : rundownTime);
            // A method only a rundown names was there since the one before it ended
            if (!isLoad) beginTime = previousRange.EndTime;
        }

        AddRange(new(startAddress, endAddress, methodIndex, beginTime, UTimeSpan.MaxValue, tier));
    }

    private void AddRange(UMethodAddressRange range)
    {
        _mapMethodAddressToRangeIndex[range.BeginAddress] = _methodAddressRanges.Count;
        _methodAddressRanges.Add(range);
        _isSorted = false;
    }

    private void Merge(ref UMethodAddressRange range, UAddress endAddress, OptimizationTier tier)
    {
        if (endAddress > range.EndAddress)
        {
            range = range with { EndAddress = endAddress };
            // the range grew: the bounds the lookups stop at (_reach) are of its old end, they are built again before the next lookup
            _isSorted = false;
        }
        if (range.Tier == OptimizationTier.Unknown && tier != OptimizationTier.Unknown)
        {
            range = range with { Tier = tier };
        }
    }

    private static void CloseCodeRange(ref UMethodAddressRange range, UTimeSpan endTime)
    {
        if (range.EndTime != UTimeSpan.MaxValue) return;
        // Never let a range end before it starts (an unload can be reported with the same timestamp as
        // the load, and a rundown event carries no meaningful load time).
        range = range with { EndTime = endTime > range.BeginTime ? endTime : range.BeginTime };
    }
}
