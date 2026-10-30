// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Model;
using Ultra.Core.Symbols;

namespace Ultra.Core.Linux;

/// <summary>
/// Resolves kernel addresses with the /proc/kallsyms text recorded in the trace.
/// </summary>
internal sealed class KernelSymbolResolver : INativeSymbolResolver
{
    /// <summary>The pseudo module path of the kernel in the trace model.</summary>
    public const string ModulePath = "[kernel.kallsyms]";

    /// <summary>Kernel addresses on x86_64 and arm64 start here; below are per-cpu and absolute symbols.</summary>
    private const ulong KernelSpaceStart = 0xffff000000000000;

    private readonly ulong[] _addresses;
    private readonly string[] _names;

    private KernelSymbolResolver(ulong[] addresses, string[] names)
    {
        _addresses = addresses;
        _names = names;
    }

    public int SymbolCount => _addresses.Length;

    /// <summary>The lowest text symbol address.</summary>
    public ulong MinAddress => _addresses[0];

    /// <summary>The address after the highest text symbol (with room for its code).</summary>
    public ulong EndAddress => _addresses[^1] + 0x10000;

    /// <summary>
    /// Parses the text of /proc/kallsyms ("address type name [module]" lines), keeping the text symbols.
    /// </summary>
    public static KernelSymbolResolver? Parse(string kallsyms)
    {
        var list = new List<(ulong Address, string Name)>();
        foreach (var line in kallsyms.AsSpan().EnumerateLines())
        {
            // ffffffff81000000 T _text
            // ffffffffc0a01000 t mod_fn\t[module]
            if (line.Length < 20) continue;
            if (!ulong.TryParse(line.Slice(0, 16), System.Globalization.NumberStyles.HexNumber, null, out var address)) continue;
            if (address < KernelSpaceStart) continue;
            var type = line[17];
            if (type != 'T' && type != 't' && type != 'W' && type != 'w') continue;
            var rest = line.Slice(19);
            int end = rest.IndexOfAny(' ', '\t', '\r');
            var name = (end >= 0 ? rest.Slice(0, end) : rest).ToString();
            if (name.Length == 0) continue;
            list.Add((address, name));
        }

        if (list.Count == 0) return null;
        list.Sort((a, b) => a.Address.CompareTo(b.Address));

        var addresses = new List<ulong>(list.Count);
        var names = new List<string>(list.Count);
        foreach (var (address, name) in list)
        {
            if (addresses.Count > 0 && addresses[^1] == address) continue;
            addresses.Add(address);
            names.Add(name);
        }
        return new KernelSymbolResolver(addresses.ToArray(), names.ToArray());
    }

    /// <inheritdoc />
    public bool TryResolve(UTraceNativeModule module, ulong address, out ulong symbolAddress, out string symbolName)
    {
        symbolAddress = 0;
        symbolName = string.Empty;

        int lo = 0, hi = _addresses.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_addresses[mid] <= address)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (found < 0 || address - _addresses[found] > 0x100000) return false;

        symbolAddress = _addresses[found];
        symbolName = _names[found];
        return true;
    }
}
