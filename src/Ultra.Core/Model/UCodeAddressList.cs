// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a list of <see cref="UAddress"/> each associated with a unique <see cref="UCodeAddressIndex"/>.
/// </summary>
public sealed class UCodeAddressList : UGenericList<UAddress>
{
    private readonly Dictionary<UAddress, UCodeAddressIndex> _mapAddressToIndex = new();

    /// <summary>
    /// Gets the code address at the specified index.
    /// </summary>
    /// <param name="index">The code address index.</param>
    /// <returns>The <see cref="UAddress"/> instance.</returns>
    public UAddress this[UCodeAddressIndex index] => List[index];

    /// <summary>
    /// Gets or creates a code address for the specified address.
    /// </summary>
    /// <param name="address">The code address.</param>
    /// <returns>The <see cref="UCodeAddressIndex"/> for the address.</returns>
    public UCodeAddressIndex GetOrCreateAddress(UAddress address)
    {
        if (_mapAddressToIndex.TryGetValue(address, out var index))
        {
            return index;
        }
        var codeAddressIndex = new UCodeAddressIndex(List.Count);
        _mapAddressToIndex.Add(address, codeAddressIndex);
        List.Add(address);
        return codeAddressIndex;
    }
}