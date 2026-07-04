// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Text;

namespace Ultra.Core.MachO;

/// <summary>
/// Reads the symbol table (LC_SYMTAB) of a Mach-O file to resolve code addresses to symbol names.
/// </summary>
/// <remarks>
/// Only symbols present in the file on disk are available. System libraries living in the dyld
/// shared cache don't have a file on disk and cannot be resolved by this reader.
/// </remarks>
internal sealed class MachOSymbolReader
{
    private const uint FatMagicBigEndian = 0xCAFEBABE;
    private const uint FatMagic64BigEndian = 0xCAFEBABF;
    private const uint MachOMagic64 = 0xFEEDFACF;

    private const int CpuTypeArm64 = 0x0100000C;
    private const int CpuTypeX64 = 0x01000007;

    private const uint LcSegment64 = 0x19;
    private const uint LcSymtab = 0x2;

    private readonly ulong _textSegmentVirtualAddress;
    private readonly MachOSymbol[] _symbols;

    private MachOSymbolReader(ulong textSegmentVirtualAddress, MachOSymbol[] symbols)
    {
        _textSegmentVirtualAddress = textSegmentVirtualAddress;
        _symbols = symbols;
    }

    /// <summary>
    /// Gets the number of symbols found in the file.
    /// </summary>
    public int SymbolCount => _symbols.Length;

    /// <summary>
    /// Tries to resolve an address to a symbol name.
    /// </summary>
    /// <param name="loadAddress">The address at which the module is loaded in the profiled process.</param>
    /// <param name="address">The address to resolve.</param>
    /// <param name="symbol">The symbol containing the address, if found.</param>
    /// <returns><c>true</c> if a symbol was found; otherwise <c>false</c>.</returns>
    public bool TryResolve(ulong loadAddress, ulong address, out MachOSymbol symbol)
    {
        symbol = default;
        if (_symbols.Length == 0)
        {
            return false;
        }

        // The module is loaded at loadAddress but linked at _textSegmentVirtualAddress
        var virtualAddress = address - loadAddress + _textSegmentVirtualAddress;

        // Binary search for the last symbol with Address <= virtualAddress
        int lo = 0, hi = _symbols.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_symbols[mid].Address <= virtualAddress)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0)
        {
            return false;
        }

        // Symbol sizes are not available, attribute the address to the nearest preceding symbol
        // (same semantics as dladdr) but reject unreasonably large gaps.
        if (virtualAddress - _symbols[found].Address > 0x1000000)
        {
            return false;
        }

        symbol = _symbols[found];
        return true;
    }

    /// <summary>
    /// Tries to read the symbols of a Mach-O file.
    /// </summary>
    /// <param name="filePath">The path of the Mach-O file.</param>
    /// <param name="reader">The symbol reader, if the file exists and could be parsed.</param>
    /// <returns><c>true</c> if the file could be parsed; otherwise <c>false</c>.</returns>
    public static bool TryRead(string filePath, out MachOSymbolReader? reader)
    {
        reader = null;
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            var data = File.ReadAllBytes(filePath);
            return TryRead(data, out reader);
        }
        catch
        {
            // Any IO or parsing error is treated as "no symbols available"
            return false;
        }
    }

    private static bool TryRead(byte[] data, out MachOSymbolReader? reader)
    {
        reader = null;
        if (data.Length < 32)
        {
            return false;
        }

        var span = data.AsSpan();
        int offset = 0;

        // Fat binary? Find the slice matching the current architecture.
        var magicBigEndian = BinaryPrimitives.ReadUInt32BigEndian(span);
        if (magicBigEndian is FatMagicBigEndian or FatMagic64BigEndian)
        {
            var is64 = magicBigEndian == FatMagic64BigEndian;
            var archCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4));
            var archSize = is64 ? 32 : 20;
            var expectedCpuType = OperatingSystem.IsMacOS() && System.Runtime.Intrinsics.Arm.ArmBase.Arm64.IsSupported ? CpuTypeArm64 : CpuTypeX64;

            long sliceOffset = -1;
            for (var i = 0; i < archCount; i++)
            {
                var archOffset = 8 + i * archSize;
                if (archOffset + archSize > span.Length) return false;
                var cpuType = BinaryPrimitives.ReadInt32BigEndian(span.Slice(archOffset));
                if (cpuType == expectedCpuType)
                {
                    sliceOffset = is64
                        ? (long)BinaryPrimitives.ReadUInt64BigEndian(span.Slice(archOffset + 8))
                        : BinaryPrimitives.ReadUInt32BigEndian(span.Slice(archOffset + 8));
                    break;
                }
            }

            if (sliceOffset < 0 || sliceOffset + 32 > span.Length)
            {
                return false;
            }

            offset = (int)sliceOffset;
        }

        // mach_header_64
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset));
        if (magic != MachOMagic64)
        {
            return false;
        }

        var commandCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 16));
        var commandOffset = offset + 32; // sizeof(mach_header_64)

        ulong textSegmentVirtualAddress = 0;
        uint symbolTableOffset = 0;
        uint symbolCount = 0;
        uint stringTableOffset = 0;
        uint stringTableSize = 0;

        for (var i = 0; i < commandCount; i++)
        {
            if (commandOffset + 8 > span.Length) return false;
            var command = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(commandOffset));
            var commandSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(commandOffset + 4));
            if (commandSize < 8 || commandOffset + commandSize > span.Length) return false;

            if (command == LcSegment64 && commandSize >= 72)
            {
                var segmentName = Encoding.ASCII.GetString(span.Slice(commandOffset + 8, 16)).TrimEnd('\0');
                if (segmentName == "__TEXT")
                {
                    textSegmentVirtualAddress = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(commandOffset + 24));
                }
            }
            else if (command == LcSymtab && commandSize >= 24)
            {
                symbolTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(commandOffset + 8));
                symbolCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(commandOffset + 12));
                stringTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(commandOffset + 16));
                stringTableSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(commandOffset + 20));
            }

            commandOffset += (int)commandSize;
        }

        if (symbolCount == 0 || symbolTableOffset == 0 || stringTableOffset == 0)
        {
            return false;
        }

        // The symbol/string table offsets are relative to the beginning of the slice
        var symbols = new List<MachOSymbol>((int)symbolCount);
        const int nlist64Size = 16;
        var stringTableStart = offset + (long)stringTableOffset;
        var stringTableEnd = stringTableStart + stringTableSize;
        if (stringTableEnd > span.Length) return false;

        for (var i = 0; i < symbolCount; i++)
        {
            var symbolOffset = offset + (long)symbolTableOffset + i * nlist64Size;
            if (symbolOffset + nlist64Size > span.Length) return false;

            var nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)symbolOffset));
            var type = span[(int)symbolOffset + 4];
            var value = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice((int)symbolOffset + 8));

            // Skip debug (stab) symbols and keep only symbols defined in a section (N_SECT)
            if ((type & 0xE0) != 0 || (type & 0x0E) != 0x0E || value == 0)
            {
                continue;
            }

            var nameOffset = stringTableStart + nameIndex;
            if (nameOffset >= stringTableEnd)
            {
                continue;
            }

            var nameSpan = span.Slice((int)nameOffset, (int)Math.Min(stringTableEnd - nameOffset, 4096));
            var nameEnd = nameSpan.IndexOf((byte)0);
            if (nameEnd <= 0)
            {
                continue;
            }

            var name = Encoding.UTF8.GetString(nameSpan.Slice(0, nameEnd));

            // Strip the leading underscore of C symbols
            if (name.StartsWith('_') && !name.StartsWith("__Z", StringComparison.Ordinal))
            {
                name = name.Substring(1);
            }

            symbols.Add(new MachOSymbol(value, name));
        }

        if (symbols.Count == 0)
        {
            return false;
        }

        symbols.Sort((a, b) => a.Address.CompareTo(b.Address));

        reader = new MachOSymbolReader(textSegmentVirtualAddress, symbols.ToArray());
        return true;
    }
}

/// <summary>
/// A symbol in a Mach-O file.
/// </summary>
/// <param name="Address">The virtual address of the symbol (as linked, not as loaded).</param>
/// <param name="Name">The name of the symbol (possibly C++ mangled).</param>
internal readonly record struct MachOSymbol(ulong Address, string Name);
