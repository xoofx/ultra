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
public class UTraceManagedMethodList : UGenericList<UTraceManagedMethod>
{
    private UnsafeDictionary<UAddress, int> _mapMethodAddressToMethodIndex = new();
    private UnsafeDictionary<long, int> _mapManagedMethodIDToMethodIndex = new();
    private UnsafeList<UAddressRange> _methodAddressRanges = new();

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
    /// <returns>The <see cref="UTraceManagedMethod"/> instance.</returns>
    public UTraceManagedMethod GetOrCreateManagedMethod(int threadID, long moduleID, long methodID, string methodNamespace, string methodName, string methodSignature, int methodToken, MethodFlags methodFlags, UAddress methodStartAddress, USize methodSize)
    {
        if (_mapManagedMethodIDToMethodIndex.TryGetValue(methodID, out var methodIndex))
        {
            return List[methodIndex];
        }
        var method = new UTraceManagedMethod(threadID, moduleID, methodID, methodNamespace, methodName, methodSignature, methodToken, methodFlags, methodStartAddress, methodSize);
        methodIndex = List.Count;
        _mapManagedMethodIDToMethodIndex.Add(methodID, methodIndex);
        List.Add(method);
        _methodAddressRanges.Add(new(methodStartAddress, methodStartAddress + methodSize, methodIndex));
        return method;
    }

    /// <summary>
    /// Sorts the method address ranges.
    /// </summary>
    public void SortMethodAddressRanges()
    {
        _methodAddressRanges.AsSpan().SortByRef(new UAddressRangeComparer());
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
    /// Tries to find a method by its address.
    /// </summary>
    /// <param name="address">The address of the method.</param>
    /// <param name="method">The method found, if any.</param>
    /// <returns>True if the method was found, otherwise false.</returns>
    public bool TryFindMethodByAddress(UAddress address, [NotNullWhen(true)] out UTraceManagedMethod? method)
    {
        var ranges = _methodAddressRanges.AsSpan();
        var comparer = new UAddressRangeFinder(address);
        var index = ranges.BinarySearch(comparer);
        if (index >= 0)
        {
            method = List[ranges[index].Index];
            return true;
        }
        method = null;
        return false;
    }
}