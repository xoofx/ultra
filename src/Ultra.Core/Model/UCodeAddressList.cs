// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// Represents a list of <see cref="UAddress"/> each associated with a unique <see cref="UCodeAddressIndex"/>.
/// </summary>
/// <remarks>
/// A code address is resolved to a managed method when the sample is recorded, because the same address
/// can be used by several collectible methods over the lifetime of the process. An address is therefore
/// interned per resolved method: an address recycled by another method gets a distinct index.
/// <para>
/// The code version the address was in (the code range of the method that was loaded there at the time of the sample: its
/// start and its optimization tier) is kept with it too, and is part of what tells two code addresses apart: a method that
/// comes back to an address after its unload - same id, same name, another compilation - is the same method in another
/// version, and only the time of the sample tells which one a frame was in.
/// </para>
/// </remarks>
public sealed class UCodeAddressList : UGenericList<UAddress>
{
    private readonly Dictionary<(UAddress Address, int ManagedMethodIndex, UAddress CodeStart, OptimizationTier Tier, bool HasCodeVersion), UCodeAddressIndex> _mapAddressToIndex = new();
    private UnsafeList<int> _managedMethodIndices = new();
    private readonly List<(UAddress CodeStart, OptimizationTier Tier, bool HasCodeVersion)> _codeVersions = new();

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
    /// The code version this code address was in when it was sampled: the start of the method's code range and its optimization
    /// tier. False for an address that is not in a managed method, or that was created without its code range.
    /// </summary>
    /// <param name="index">The code address index.</param>
    /// <param name="codeStart">The first address of the code of the version.</param>
    /// <param name="tier">The optimization tier of the version.</param>
    public bool TryGetCodeVersion(UCodeAddressIndex index, out UAddress codeStart, out OptimizationTier tier)
    {
        var version = _codeVersions[index.Value];
        codeStart = version.CodeStart;
        tier = version.Tier;
        return version.HasCodeVersion;
    }

    /// <summary>
    /// Gets or creates a code address for the specified address and managed method.
    /// </summary>
    /// <param name="address">The code address.</param>
    /// <param name="managedMethodIndex">The index of the managed method loaded at this address, or -1 if none.</param>
    /// <returns>The <see cref="UCodeAddressIndex"/> for the address.</returns>
    public UCodeAddressIndex GetOrCreateAddress(UAddress address, int managedMethodIndex)
        => GetOrCreateAddress(address, managedMethodIndex, default, OptimizationTier.Unknown, false);

    /// <summary>
    /// Gets or creates a code address for the specified address in the code range of a managed method that was loaded there
    /// at the time of the sample (<see cref="UTraceManagedMethodList.TryFindCodeRangeByAddress"/>).
    /// </summary>
    /// <param name="address">The code address.</param>
    /// <param name="codeRange">The code range of the managed method the address was in.</param>
    /// <returns>The <see cref="UCodeAddressIndex"/> for the address.</returns>
    public UCodeAddressIndex GetOrCreateAddress(UAddress address, in UMethodAddressRange codeRange)
        => GetOrCreateAddress(address, codeRange.Index, codeRange.BeginAddress, codeRange.Tier, true);

    private UCodeAddressIndex GetOrCreateAddress(UAddress address, int managedMethodIndex, UAddress codeStart, OptimizationTier tier, bool hasCodeVersion)
    {
        var key = (address, managedMethodIndex, codeStart, tier, hasCodeVersion);
        if (_mapAddressToIndex.TryGetValue(key, out var index))
        {
            return index;
        }
        var codeAddressIndex = new UCodeAddressIndex(List.Count);
        _mapAddressToIndex.Add(key, codeAddressIndex);
        List.Add(address);
        _managedMethodIndices.Add(managedMethodIndex);
        _codeVersions.Add((codeStart, tier, hasCodeVersion));
        return codeAddressIndex;
    }
}
