// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Ultra.Core.Linux;

/// <summary>
/// The kind of a frame in a .ultraperf file.
/// </summary>
internal enum UltraPerfFrameKind : byte
{
    /// <summary>A raw perf record (header included) read from the ring buffer of the frame's CPU and event.</summary>
    PerfRecord = 1,

    /// <summary>An executable mapping of a profiled process taken from /proc/[pid]/maps when profiling started (see <see cref="UltraPerfPayload"/>).</summary>
    MapSnapshot = 2,

    /// <summary>The name of a process or thread taken from /proc when profiling started.</summary>
    CommSnapshot = 3,

    /// <summary>The content of /proc/kallsyms of the recording machine.</summary>
    Kallsyms = 4,

    /// <summary>The ELF build id of a mapped file.</summary>
    BuildId = 5,

    /// <summary>The last frame: counters of the recording.</summary>
    End = 6,

    /// <summary>The name of a recorded event (event index + name), e.g. "cpu-clock" or "cache-misses".</summary>
    EventName = 7,
}

/// <summary>
/// The header of a .ultraperf file.
/// </summary>
internal sealed class UltraPerfHeader
{
    public const int Version = 1;
    public const int Size = 64;
    public static ReadOnlySpan<byte> Magic => "ULTRAPRF"u8;

    public int CpuCount { get; set; }

    /// <summary>The sampling period of the first event in nanoseconds.</summary>
    public ulong SamplingPeriodNs { get; set; }

    /// <summary>CLOCK_MONOTONIC when the file was created, in nanoseconds (the clock of the perf records).</summary>
    public ulong StartMonotonicNs { get; set; }

    /// <summary>UTC time when the file was created (<see cref="DateTime.Ticks"/>).</summary>
    public long StartUtcTicks { get; set; }

    /// <summary>The perf_event_attr.sample_type used by all the events (defines the layout of the sample records).</summary>
    public ulong SampleType { get; set; }

    /// <summary>The architecture of the recording machine (null when unknown).</summary>
    public System.Runtime.InteropServices.Architecture? ProcessArchitecture { get; set; }

    /// <summary>The process ids that were requested (samples of other processes were dropped while recording).</summary>
    public List<int> ProcessIds { get; } = new();

    /// <summary>The attributes of the recorded events (index = eventIndex of the frames).</summary>
    public List<PerfEventAttr> Events { get; } = new();

    public void Write(Stream stream)
    {
        Span<byte> header = stackalloc byte[Size];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12), Size);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16), (uint)CpuCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(20), (uint)Events.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24), (uint)ProcessIds.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28), ProcessArchitecture is { } arch ? (uint)arch + 1 : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(32), SamplingPeriodNs);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(40), StartMonotonicNs);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(48), StartUtcTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(56), SampleType);
        stream.Write(header);

        Span<byte> pid = stackalloc byte[4];
        foreach (var processId in ProcessIds)
        {
            BinaryPrimitives.WriteInt32LittleEndian(pid, processId);
            stream.Write(pid);
        }

        foreach (var attr in Events)
        {
            var local = attr;
            stream.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<PerfEventAttr>(ref local)));
        }
    }

    public static UltraPerfHeader Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[Size];
        stream.ReadExactly(header);
        if (!header.Slice(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("Not a .ultraperf file (invalid magic)");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8));
        if (version != Version) throw new InvalidDataException($"Unsupported .ultraperf version {version} (expected {Version})");
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12));
        if (headerSize < Size) throw new InvalidDataException("Invalid .ultraperf header size");

        var result = new UltraPerfHeader
        {
            CpuCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16)),
            SamplingPeriodNs = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(32)),
            StartMonotonicNs = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(40)),
            StartUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(48)),
            SampleType = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(56)),
        };
        var archPlusOne = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(28));
        result.ProcessArchitecture = archPlusOne == 0 ? null : (System.Runtime.InteropServices.Architecture)(archPlusOne - 1);
        var eventCount = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20));
        var pidCount = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24));

        // Skip a possible larger header of a newer minor layout
        if (headerSize > Size) stream.ReadExactly(new byte[headerSize - Size]);

        Span<byte> pid = stackalloc byte[4];
        for (int i = 0; i < pidCount; i++)
        {
            stream.ReadExactly(pid);
            result.ProcessIds.Add(BinaryPrimitives.ReadInt32LittleEndian(pid));
        }

        Span<byte> attrBytes = stackalloc byte[PerfEventAttr.Size];
        for (int i = 0; i < eventCount; i++)
        {
            stream.ReadExactly(attrBytes);
            result.Events.Add(MemoryMarshal.Read<PerfEventAttr>(attrBytes));
        }

        return result;
    }
}

/// <summary>
/// A frame read from a .ultraperf file. The payload lives in the reader's buffer: valid until the next frame is read (copy it to keep it).
/// </summary>
internal readonly record struct UltraPerfFrame(UltraPerfFrameKind Kind, byte EventIndex, ushort Cpu, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Writes a .ultraperf file: a header followed by frames (8-byte frame header: kind, event index, cpu, payload length).
/// A frame is written whole under a lock: the reader thread writes the records while the main thread writes the snapshot
/// of a process launched after the recording started.
/// </summary>
internal sealed class UltraPerfFileWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly byte[] _frameHeader = new byte[8];
    private readonly object _lock = new();
    private long _length;

    public UltraPerfFileWriter(string path, UltraPerfHeader header)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        header.Write(_stream);
        _length = _stream.Position;
    }

    public string Path { get; }

    /// <summary>The number of bytes written so far (readable from another thread, for progress).</summary>
    public long Length => Volatile.Read(ref _length);

    public void WriteFrame(UltraPerfFrameKind kind, byte eventIndex, ushort cpu, ReadOnlySpan<byte> payload)
    {
        lock (_lock)
        {
            _frameHeader[0] = (byte)kind;
            _frameHeader[1] = eventIndex;
            BinaryPrimitives.WriteUInt16LittleEndian(_frameHeader.AsSpan(2), cpu);
            BinaryPrimitives.WriteUInt32LittleEndian(_frameHeader.AsSpan(4), (uint)payload.Length);
            _stream.Write(_frameHeader);
            _stream.Write(payload);
            Volatile.Write(ref _length, _length + 8 + payload.Length);
        }
    }

    public void WriteRecord(ushort cpu, byte eventIndex, ReadOnlySpan<byte> record) => WriteFrame(UltraPerfFrameKind.PerfRecord, eventIndex, cpu, record);

    public void Dispose()
    {
        lock (_lock)
        {
            _stream.Dispose();
        }
    }
}

/// <summary>
/// Reads a .ultraperf file.
/// </summary>
internal sealed class UltraPerfFileReader : IDisposable
{
    private readonly Stream _stream;

    public UltraPerfFileReader(string path) : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
    {
    }

    public UltraPerfFileReader(Stream stream)
    {
        _stream = stream;
        try
        {
            Header = UltraPerfHeader.Read(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public UltraPerfHeader Header { get; }

    /// <summary>The bytes read so far (for progress).</summary>
    public long Position => _stream.Position;

    /// <summary>The size of the file.</summary>
    public long Length => _stream.Length;

    /// <summary>
    /// Reads the frames in file order, each payload in a buffer reused for the next frame (millions of records: no allocation per frame).
    /// A truncated last frame (the profiler was killed) ends the enumeration.
    /// </summary>
    public IEnumerable<UltraPerfFrame> ReadFrames()
    {
        var frameHeader = new byte[8];
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int read = _stream.ReadAtLeast(frameHeader, 8, throwOnEndOfStream: false);
            if (read < 8) yield break;
            var kind = (UltraPerfFrameKind)frameHeader[0];
            var eventIndex = frameHeader[1];
            var cpu = BinaryPrimitives.ReadUInt16LittleEndian(frameHeader.AsSpan(2));
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(frameHeader.AsSpan(4));
            if (length > buffer.Length) buffer = new byte[Math.Max(length, buffer.Length * 2)];
            if (_stream.ReadAtLeast(buffer.AsSpan(0, length), length, throwOnEndOfStream: false) < length) yield break;
            yield return new UltraPerfFrame(kind, eventIndex, cpu, new ReadOnlyMemory<byte>(buffer, 0, length));
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}

/// <summary>
/// Encodes and decodes the payloads of the non-record frames.
/// </summary>
internal static class UltraPerfPayload
{
    /// <summary>An executable mapping: pid, start, end, file offset, path.</summary>
    public readonly record struct Map(int Pid, ulong Start, ulong End, ulong Offset, string Path);

    /// <summary>A process (Tid == Pid) or thread name.</summary>
    public readonly record struct Comm(int Pid, int Tid, string Name);

    /// <summary>The ELF build id of a file.</summary>
    public readonly record struct BuildId(string Path, byte[] Id);

    /// <summary>The counters written in the last frame.</summary>
    public readonly record struct End(ulong LostRecords, ulong Records, ulong DroppedSamples, ulong EndMonotonicNs);

    public static byte[] EncodeMap(Map map)
    {
        var path = Encoding.UTF8.GetBytes(map.Path);
        var result = new byte[28 + path.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, map.Pid);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(4), map.Start);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(12), map.End);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(20), map.Offset);
        path.CopyTo(result, 28);
        return result;
    }

    public static Map DecodeMap(ReadOnlySpan<byte> payload) => new(
        BinaryPrimitives.ReadInt32LittleEndian(payload),
        BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(4)),
        BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(12)),
        BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(20)),
        Encoding.UTF8.GetString(payload.Slice(28)));

    public static byte[] EncodeComm(Comm comm)
    {
        var name = Encoding.UTF8.GetBytes(comm.Name);
        var result = new byte[8 + name.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, comm.Pid);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), comm.Tid);
        name.CopyTo(result, 8);
        return result;
    }

    public static Comm DecodeComm(ReadOnlySpan<byte> payload) => new(
        BinaryPrimitives.ReadInt32LittleEndian(payload),
        BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4)),
        Encoding.UTF8.GetString(payload.Slice(8)));

    public static byte[] EncodeBuildId(BuildId buildId)
    {
        var path = Encoding.UTF8.GetBytes(buildId.Path);
        var result = new byte[2 + buildId.Id.Length + path.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)buildId.Id.Length);
        buildId.Id.CopyTo(result, 2);
        path.CopyTo(result, 2 + buildId.Id.Length);
        return result;
    }

    public static BuildId DecodeBuildId(ReadOnlySpan<byte> payload)
    {
        int idLength = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        return new(Encoding.UTF8.GetString(payload.Slice(2 + idLength)), payload.Slice(2, idLength).ToArray());
    }

    public static byte[] EncodeEventName(byte eventIndex, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        var result = new byte[1 + bytes.Length];
        result[0] = eventIndex;
        bytes.CopyTo(result, 1);
        return result;
    }

    public static (byte EventIndex, string Name) DecodeEventName(ReadOnlySpan<byte> payload) => (payload[0], Encoding.UTF8.GetString(payload.Slice(1)));

    public static byte[] EncodeEnd(End end)
    {
        var result = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(result, end.LostRecords);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), end.Records);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), end.DroppedSamples);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24), end.EndMonotonicNs);
        return result;
    }

    public static End DecodeEnd(ReadOnlySpan<byte> payload) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(payload),
        BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8)),
        BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(16)),
        BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(24)));
}
