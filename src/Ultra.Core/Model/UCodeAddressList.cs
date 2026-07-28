// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// Represents a list of <see cref="UAddress"/> each associated with a unique <see cref="UCodeAddressIndex"/>.
/// </summary>
/// <remarks>
/// A code address is resolved to a managed method when the sample is recorded, because the same address
/// can be used by several collectible methods over the lifetime of the process. An address is therefore
/// interned per resolved method: an address recycled by another method gets a distinct index.
/// </remarks>
public sealed class UCodeAddressList : UGenericList<UAddress>
{
    private readonly Dictionary<(UAddress Address, int ManagedMethodIndex), UCodeAddressIndex> _mapAddressToIndex = new();
    private UnsafeList<int> _managedMethodIndices = new();

    /// <summary>
    /// Gets the code address at the specified index.
    /// </summary>
    /// <param name="index">The code address index.</param>
    /// <returns>The <see cref="UAddress"/> instance.</returns>
    public UAddress this[UCodeAddressIndex index] => List[index];

    /// <summary>
    /// Gets the index in <see cref="UTraceManagedMethodList"/> of the managed method this code address
    /// belongs to, or -1 if the address does not belong to a managed method.
    /// </summary>
    /// <param name="index">The code address index.</param>
    /// <returns>The managed method index, or -1.</returns>
    public int GetManagedMethodIndex(UCodeAddressIndex index) => _managedMethodIndices[index];

    /// <summary>
    /// Gets or creates a code address for the specified address and managed method.
    /// </summary>
    /// <param name="address">The code address.</param>
    /// <param name="managedMethodIndex">The index of the managed method loaded at this address, or -1 if none.</param>
    /// <returns>The <see cref="UCodeAddressIndex"/> for the address.</returns>
    public UCodeAddressIndex GetOrCreateAddress(UAddress address, int managedMethodIndex)
    {
        var key = (address, managedMethodIndex);
        if (_mapAddressToIndex.TryGetValue(key, out var index))
        {
            return index;
        }
        var codeAddressIndex = new UCodeAddressIndex(List.Count);
        _mapAddressToIndex.Add(key, codeAddressIndex);
        List.Add(address);
        _managedMethodIndices.Add(managedMethodIndex);
        return codeAddressIndex;
    }
}
