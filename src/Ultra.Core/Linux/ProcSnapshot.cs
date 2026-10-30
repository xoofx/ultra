// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Globalization;

namespace Ultra.Core.Linux;

/// <summary>
/// Reads from /proc what the perf event stream does not provide for processes that already run when profiling starts:
/// their executable mappings, their process and thread names, the kernel symbols, and the build ids of the mapped files.
/// </summary>
internal static class ProcSnapshot
{
    /// <summary>
    /// Parses a line of /proc/[pid]/maps. Returns false for non-executable or malformed lines.
    /// </summary>
    public static bool TryParseExecutableMapsLine(int pid, string line, out UltraPerfPayload.Map map)
    {
        // 7f0a1c000000-7f0a1c021000 r-xp 00000000 08:01 1234    /usr/lib/x86_64-linux-gnu/libc.so.6
        map = default;
        var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts[1].Length < 4 || parts[1][2] != 'x') return false;
        int dash = parts[0].IndexOf('-');
        if (dash <= 0) return false;
        if (!ulong.TryParse(parts[0].AsSpan(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start)) return false;
        if (!ulong.TryParse(parts[0].AsSpan(dash + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end)) return false;
        if (!ulong.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset)) return false;
        var path = parts.Length > 5 ? parts[5].Trim() : string.Empty;
        map = new UltraPerfPayload.Map(pid, start, end, offset, path);
        return true;
    }

    /// <summary>
    /// The executable mappings of a process (empty when the process is gone or not readable).
    /// </summary>
    public static List<UltraPerfPayload.Map> ReadExecutableMaps(int pid)
    {
        var maps = new List<UltraPerfPayload.Map>();
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/maps"))
            {
                if (TryParseExecutableMapsLine(pid, line, out var map)) maps.Add(map);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return maps;
    }

    /// <summary>
    /// The process name (tid == pid) and the thread names of a process.
    /// </summary>
    public static List<UltraPerfPayload.Comm> ReadComms(int pid)
    {
        var comms = new List<UltraPerfPayload.Comm>();
        try
        {
            comms.Add(new UltraPerfPayload.Comm(pid, pid, ReadComm($"/proc/{pid}/comm")));
            foreach (var taskDir in Directory.EnumerateDirectories($"/proc/{pid}/task"))
            {
                if (!int.TryParse(Path.GetFileName(taskDir), out var tid) || tid == pid) continue;
                comms.Add(new UltraPerfPayload.Comm(pid, tid, ReadComm($"{taskDir}/comm")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return comms;
    }

    private static string ReadComm(string path)
    {
        try
        {
            return File.ReadAllText(path).TrimEnd('\n', '\0');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The content of /proc/kallsyms (empty when not readable: kptr_restrict without root).
    /// </summary>
    public static string ReadKallsyms()
    {
        try
        {
            var text = File.ReadAllText("/proc/kallsyms");
            // With kptr_restrict (and no root) the addresses are all zero: useless
            return text.StartsWith("0000000000000000 ", StringComparison.Ordinal) ? string.Empty : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Reads the GNU build id of an ELF64 file (the NT_GNU_BUILD_ID note), or null.
    /// </summary>
    public static byte[]? TryReadElfBuildId(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
            return TryReadElfBuildId(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the GNU build id of an ELF64 little-endian image from its PT_NOTE segments, or null.
    /// </summary>
    public static byte[]? TryReadElfBuildId(Stream stream)
    {
        Span<byte> ident = stackalloc byte[64];
        if (stream.ReadAtLeast(ident, 64, throwOnEndOfStream: false) < 64) return null;
        if (ident[0] != 0x7f || ident[1] != (byte)'E' || ident[2] != (byte)'L' || ident[3] != (byte)'F') return null;
        if (ident[4] != 2 || ident[5] != 1) return null; // ELF64, little endian only

        var phoff = BinaryPrimitives.ReadUInt64LittleEndian(ident.Slice(32));
        var phentsize = BinaryPrimitives.ReadUInt16LittleEndian(ident.Slice(54));
        var phnum = BinaryPrimitives.ReadUInt16LittleEndian(ident.Slice(56));
        if (phentsize < 56 || phnum == 0 || phoff > (ulong)stream.Length) return null;

        Span<byte> phdr = stackalloc byte[56];
        for (int i = 0; i < phnum; i++)
        {
            stream.Position = (long)phoff + (long)i * phentsize;
            if (stream.ReadAtLeast(phdr, 56, throwOnEndOfStream: false) < 56) return null;
            if (BinaryPrimitives.ReadUInt32LittleEndian(phdr) != 4) continue; // PT_NOTE
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(phdr.Slice(8));
            var filesz = BinaryPrimitives.ReadUInt64LittleEndian(phdr.Slice(32));
            if (filesz == 0 || filesz > 1 << 20 || offset + filesz > (ulong)stream.Length) continue;

            var notes = new byte[filesz];
            stream.Position = (long)offset;
            if (stream.ReadAtLeast(notes, notes.Length, throwOnEndOfStream: false) < notes.Length) return null;
            var buildId = FindBuildIdNote(notes);
            if (buildId is not null) return buildId;
        }

        return null;
    }

    private static byte[]? FindBuildIdNote(ReadOnlySpan<byte> notes)
    {
        int pos = 0;
        while (pos + 12 <= notes.Length)
        {
            int namesz = (int)BinaryPrimitives.ReadUInt32LittleEndian(notes.Slice(pos));
            int descsz = (int)BinaryPrimitives.ReadUInt32LittleEndian(notes.Slice(pos + 4));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(notes.Slice(pos + 8));
            pos += 12;
            int nameEnd = pos + ((namesz + 3) & ~3);
            int descEnd = nameEnd + ((descsz + 3) & ~3);
            if (namesz < 0 || descsz < 0 || descEnd > notes.Length) return null;
            if (type == 3 && namesz == 4 && notes.Slice(pos, 4).SequenceEqual("GNU\0"u8))
            {
                return notes.Slice(nameEnd, descsz).ToArray();
            }
            pos = descEnd;
        }
        return null;
    }
}
