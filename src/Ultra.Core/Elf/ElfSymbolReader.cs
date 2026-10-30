// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Text;
using Ultra.Core.Model;
using Ultra.Core.Symbols;

namespace Ultra.Core.Elf;

/// <summary>
/// Reads the function symbols (.symtab, or .dynsym for a stripped file) of an ELF64 little-endian file to resolve
/// code addresses to symbol names.
/// </summary>
/// <remarks>
/// A module is recorded as the executable mapping of the file (start address + file offset), so an address is first
/// converted to a file offset, then to the linked virtual address through the PT_LOAD segment containing it.
/// </remarks>
internal sealed class ElfSymbolReader : INativeSymbolResolver
{
    private const uint PtLoad = 1;
    private const uint ShtSymtab = 2;
    private const uint ShtDynsym = 11;
    private const byte SttFunc = 2;
    private const byte SttGnuIFunc = 10;

    private readonly Segment[] _loadSegments;
    private readonly ulong[] _addresses;
    private readonly ulong[] _sizes;
    private readonly string[] _names;

    private readonly record struct Segment(ulong FileOffset, ulong FileSize, ulong VirtualAddress);

    private ElfSymbolReader(Segment[] loadSegments, ulong[] addresses, ulong[] sizes, string[] names)
    {
        _loadSegments = loadSegments;
        _addresses = addresses;
        _sizes = sizes;
        _names = names;
    }

    /// <summary>
    /// Gets the number of function symbols found in the file.
    /// </summary>
    public int SymbolCount => _addresses.Length;

    /// <summary>
    /// Gets the function symbols (linked virtual address, size, name) sorted by address.
    /// </summary>
    public IEnumerable<(ulong Address, ulong Size, string Name)> Symbols
    {
        get
        {
            for (int i = 0; i < _addresses.Length; i++)
            {
                yield return (_addresses[i], _sizes[i], _names[i]);
            }
        }
    }

    /// <summary>
    /// Gets the PT_LOAD segments (file offset, file size, virtual address).
    /// </summary>
    public IEnumerable<(ulong FileOffset, ulong FileSize, ulong VirtualAddress)> LoadSegments => _loadSegments.Select(x => (x.FileOffset, x.FileSize, x.VirtualAddress));

    /// <inheritdoc />
    public bool TryResolve(UTraceNativeModule module, ulong address, out ulong symbolAddress, out string symbolName)
    {
        symbolAddress = 0;
        symbolName = string.Empty;
        if (_addresses.Length == 0 || address < module.BaseAddress) return false;

        var fileOffset = module.FileOffset + (address - module.BaseAddress.Value);
        if (!TryGetVirtualAddress(fileOffset, out var virtualAddress)) return false;

        // Last symbol with Address <= virtualAddress
        int lo = 0, hi = _addresses.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_addresses[mid] <= virtualAddress)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (found < 0) return false;

        var size = _sizes[found];
        if (size > 0)
        {
            // The address is in a function the symbol table does not know (stripped file with only its exports)
            if (virtualAddress >= _addresses[found] + size) return false;
        }
        else if (virtualAddress - _addresses[found] > 0x100000)
        {
            // Size unknown: same semantics as dladdr, but reject unreasonably large gaps
            return false;
        }

        symbolAddress = _addresses[found];
        symbolName = _names[found];
        return true;
    }

    private bool TryGetVirtualAddress(ulong fileOffset, out ulong virtualAddress)
    {
        foreach (var segment in _loadSegments)
        {
            if (fileOffset >= segment.FileOffset && fileOffset < segment.FileOffset + segment.FileSize)
            {
                virtualAddress = segment.VirtualAddress + (fileOffset - segment.FileOffset);
                return true;
            }
        }
        virtualAddress = 0;
        return false;
    }

    /// <summary>
    /// Tries to read the symbols of an ELF file.
    /// </summary>
    public static bool TryRead(string filePath, out ElfSymbolReader? reader)
    {
        reader = null;
        try
        {
            if (!File.Exists(filePath)) return false;
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            reader = TryRead(stream);
            return reader is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tries to read the symbols of an ELF64 little-endian image.
    /// </summary>
    public static ElfSymbolReader? TryRead(Stream stream)
    {
        Span<byte> header = stackalloc byte[64];
        if (stream.ReadAtLeast(header, 64, throwOnEndOfStream: false) < 64) return null;
        if (header[0] != 0x7f || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F') return null;
        if (header[4] != 2 || header[5] != 1) return null; // ELF64, little endian

        var phoff = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(32));
        var shoff = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(40));
        var phentsize = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(54));
        var phnum = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(56));
        var shentsize = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(58));
        var shnum = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(60));
        var length = (ulong)stream.Length;

        // PT_LOAD segments
        var segments = new List<Segment>();
        if (phentsize >= 56 && phnum > 0 && phoff + (ulong)phnum * phentsize <= length)
        {
            Span<byte> phdr = stackalloc byte[56];
            for (int i = 0; i < phnum; i++)
            {
                stream.Position = (long)(phoff + (ulong)i * phentsize);
                if (stream.ReadAtLeast(phdr, 56, throwOnEndOfStream: false) < 56) return null;
                if (BinaryPrimitives.ReadUInt32LittleEndian(phdr) != PtLoad) continue;
                segments.Add(new Segment(
                    BinaryPrimitives.ReadUInt64LittleEndian(phdr.Slice(8)),
                    BinaryPrimitives.ReadUInt64LittleEndian(phdr.Slice(32)),
                    BinaryPrimitives.ReadUInt64LittleEndian(phdr.Slice(16))));
            }
        }
        if (segments.Count == 0) return null;

        // Section headers: find .symtab (preferred, it includes the dynamic symbols) or .dynsym
        if (shentsize < 64 || shnum == 0 || shoff + (ulong)shnum * shentsize > length) return null;
        var sections = new byte[shnum * 64];
        var sectionHeaders = new (uint Type, ulong Offset, ulong Size, uint Link, ulong EntrySize)[shnum];
        Span<byte> shdr = stackalloc byte[64];
        for (int i = 0; i < shnum; i++)
        {
            stream.Position = (long)(shoff + (ulong)i * shentsize);
            if (stream.ReadAtLeast(shdr, 64, throwOnEndOfStream: false) < 64) return null;
            sectionHeaders[i] = (
                BinaryPrimitives.ReadUInt32LittleEndian(shdr.Slice(4)),
                BinaryPrimitives.ReadUInt64LittleEndian(shdr.Slice(24)),
                BinaryPrimitives.ReadUInt64LittleEndian(shdr.Slice(32)),
                BinaryPrimitives.ReadUInt32LittleEndian(shdr.Slice(40)),
                BinaryPrimitives.ReadUInt64LittleEndian(shdr.Slice(56)));
        }

        int symbolSection = Array.FindIndex(sectionHeaders, s => s.Type == ShtSymtab);
        if (symbolSection < 0) symbolSection = Array.FindIndex(sectionHeaders, s => s.Type == ShtDynsym);
        if (symbolSection < 0) return null;

        var (_, symOffset, symSize, strIndex, symEntrySize) = sectionHeaders[symbolSection];
        if (symEntrySize == 0) symEntrySize = 24;
        if (strIndex >= sectionHeaders.Length || symOffset + symSize > length) return null;
        var (_, strOffset, strSize, _, _) = sectionHeaders[strIndex];
        if (strOffset + strSize > length || strSize > int.MaxValue) return null;

        var strings = new byte[strSize];
        stream.Position = (long)strOffset;
        if (stream.ReadAtLeast(strings, strings.Length, throwOnEndOfStream: false) < strings.Length) return null;

        var symbols = new byte[symSize];
        stream.Position = (long)symOffset;
        if (stream.ReadAtLeast(symbols, symbols.Length, throwOnEndOfStream: false) < symbols.Length) return null;

        var list = new List<(ulong Address, ulong Size, string Name)>();
        for (ulong pos = 0; pos + 24 <= symSize; pos += symEntrySize)
        {
            var entry = symbols.AsSpan((int)pos, 24);
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var type = (byte)(entry[4] & 0xF);
            var sectionIndex = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(6));
            var value = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(8));
            var size = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(16));
            if ((type != SttFunc && type != SttGnuIFunc) || sectionIndex == 0 || value == 0 || nameOffset >= strSize) continue;
            list.Add((value, size, ReadCString(strings, (int)nameOffset)));
        }

        // Aliases share an address (glibc: malloc and __libc_malloc): keep the largest, then the public looking name
        list.Sort((a, b) =>
        {
            if (a.Address != b.Address) return a.Address.CompareTo(b.Address);
            if (a.Size != b.Size) return b.Size.CompareTo(a.Size);
            int underscores = CountLeadingUnderscores(a.Name).CompareTo(CountLeadingUnderscores(b.Name));
            return underscores != 0 ? underscores : string.CompareOrdinal(a.Name, b.Name);
        });

        // Keep one symbol per address
        var addresses = new List<ulong>(list.Count);
        var sizes = new List<ulong>(list.Count);
        var names = new List<string>(list.Count);
        foreach (var (address, size, name) in list)
        {
            if (addresses.Count > 0 && addresses[^1] == address) continue;
            addresses.Add(address);
            sizes.Add(size);
            names.Add(name);
        }

        return new ElfSymbolReader(segments.ToArray(), addresses.ToArray(), sizes.ToArray(), names.ToArray());
    }

    private static int CountLeadingUnderscores(string name)
    {
        int count = 0;
        while (count < name.Length && name[count] == '_') count++;
        return count;
    }

    private static string ReadCString(byte[] strings, int offset)
    {
        int end = Array.IndexOf(strings, (byte)0, offset);
        if (end < 0) end = strings.Length;
        return Encoding.UTF8.GetString(strings, offset, end - offset);
    }
}
