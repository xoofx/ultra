// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Text;
using Ultra.Core.Linux;

namespace Ultra.Tests;

/// <summary>
/// Tests of the Linux perf recording pieces that do not need a Linux kernel.
/// </summary>
[TestClass]
public class UltraPerfLinuxTests
{
    [TestMethod]
    public unsafe void RingReaderHandlesRecordsWrappingAroundTheDataArea()
    {
        // 64 byte data area, records of 24 bytes: A at 0, B at 24, C at 48 (wraps: 16 bytes at the end, 8 at the start)
        var data = new byte[64];
        var a = MakeRecord(1, 24, 0xA0);
        var b = MakeRecord(2, 24, 0xB0);
        var c = MakeRecord(3, 24, 0xC0);
        a.CopyTo(data, 0);
        b.CopyTo(data, 24);
        c.AsSpan(0, 16).CopyTo(data.AsSpan(48));
        c.AsSpan(16, 8).CopyTo(data.AsSpan(0)); // overwrites the start of A, already consumed

        var records = new List<byte[]>();
        var scratch = new byte[PerfRingReader.MaxRecordSize + 8];
        ulong newTail;
        fixed (byte* p = data)
        {
            newTail = PerfRingReader.Drain(p, 64, tail: 24, head: 72, scratch, record => records.Add(record.ToArray()));
        }

        Assert.AreEqual(72UL, newTail);
        Assert.AreEqual(2, records.Count);
        CollectionAssert.AreEqual(b, records[0]);
        CollectionAssert.AreEqual(c, records[1]);
    }

    [TestMethod]
    public unsafe void RingReaderHandlesHeaderWrappingAroundTheDataArea()
    {
        // Record of 24 bytes at 60: 4 bytes of header at the end, the rest at the start
        var data = new byte[64];
        var rec = MakeRecord(9, 24, 0x5A);
        rec.AsSpan(0, 4).CopyTo(data.AsSpan(60));
        rec.AsSpan(4, 20).CopyTo(data.AsSpan(0));

        var records = new List<byte[]>();
        var scratch = new byte[PerfRingReader.MaxRecordSize + 8];
        ulong newTail;
        fixed (byte* p = data)
        {
            newTail = PerfRingReader.Drain(p, 64, tail: 60, head: 84, scratch, record => records.Add(record.ToArray()));
        }

        Assert.AreEqual(84UL, newTail);
        Assert.AreEqual(1, records.Count);
        CollectionAssert.AreEqual(rec, records[0]);
    }

    [TestMethod]
    public unsafe void RingReaderStopsAtAPartiallyWrittenRecord()
    {
        var data = new byte[64];
        var rec = MakeRecord(9, 24, 0x11);
        rec.CopyTo(data, 0);
        var records = new List<byte[]>();
        var scratch = new byte[PerfRingReader.MaxRecordSize + 8];
        ulong newTail;
        fixed (byte* p = data)
        {
            // head is in the middle of the record
            newTail = PerfRingReader.Drain(p, 64, tail: 0, head: 20, scratch, record => records.Add(record.ToArray()));
        }

        Assert.AreEqual(0UL, newTail);
        Assert.AreEqual(0, records.Count);
    }

    [TestMethod]
    public void UltraPerfFileRoundTrip()
    {
        var header = new UltraPerfHeader
        {
            CpuCount = 32,
            SamplingPeriodNs = 122100,
            StartMonotonicNs = 123456789,
            StartUtcTicks = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc).Ticks,
            SampleType = PerfEventCollector.SampleType,
        };
        header.ProcessIds.AddRange([42, 4242]);
        header.Events.Add(PerfEventCollector.CreateCpuClockAttr(122100));

        var path = Path.Combine(Path.GetTempPath(), $"ultra_test_{Guid.NewGuid():N}.ultraperf");
        UltraPerfFileReader? file = null;
        try
        {
            var record = MakeRecord(9, 40, 0x77);
            var map = new UltraPerfPayload.Map(42, 0x7f00_0000, 0x7f10_0000, 0x1000, "/usr/lib/x86_64-linux-gnu/libc.so.6");
            var comm = new UltraPerfPayload.Comm(42, 43, "worker-1");
            var buildId = new UltraPerfPayload.BuildId("/usr/lib/libcoreclr.so", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]);
            var end = new UltraPerfPayload.End(3, 1000, 50, 999999);

            long written;
            using (var writer = new UltraPerfFileWriter(path, header))
            {
                writer.WriteFrame(UltraPerfFrameKind.MapSnapshot, 0, 0, UltraPerfPayload.EncodeMap(map));
                writer.WriteFrame(UltraPerfFrameKind.CommSnapshot, 0, 0, UltraPerfPayload.EncodeComm(comm));
                writer.WriteFrame(UltraPerfFrameKind.BuildId, 0, 0, UltraPerfPayload.EncodeBuildId(buildId));
                writer.WriteFrame(UltraPerfFrameKind.Kallsyms, 0, 0, "ffffffff81000000 T _text\n"u8);
                writer.WriteRecord(7, 0, record);
                writer.WriteFrame(UltraPerfFrameKind.End, 0, 0, UltraPerfPayload.EncodeEnd(end));
                written = writer.Length;
            }
            Assert.AreEqual(new FileInfo(path).Length, written, "the writer tracks the bytes written");

            using var reader = new UltraPerfFileReader(path);
            file = reader;
            Assert.AreEqual(32, reader.Header.CpuCount);
            Assert.AreEqual(122100UL, reader.Header.SamplingPeriodNs);
            Assert.AreEqual(123456789UL, reader.Header.StartMonotonicNs);
            Assert.AreEqual(header.StartUtcTicks, reader.Header.StartUtcTicks);
            Assert.AreEqual(PerfEventCollector.SampleType, reader.Header.SampleType);
            CollectionAssert.AreEqual(new[] { 42, 4242 }, reader.Header.ProcessIds);
            Assert.AreEqual(1, reader.Header.Events.Count);
            Assert.AreEqual(PerfEventNative.PERF_TYPE_SOFTWARE, reader.Header.Events[0].type);
            Assert.AreEqual(122100UL, reader.Header.Events[0].sample_period_or_freq);
            Assert.AreEqual(PerfEventAttr.Size, System.Runtime.CompilerServices.Unsafe.SizeOf<PerfEventAttr>(), "the attr struct size must match the size written in the file and passed to the kernel");

            // the payload buffer is reused from frame to frame: copy to keep the frames
            var frames = reader.ReadFrames().Select(f => f with { Payload = f.Payload.ToArray() }).ToList();
            Assert.AreEqual(6, frames.Count);
            Assert.AreEqual(UltraPerfFrameKind.MapSnapshot, frames[0].Kind);
            Assert.AreEqual(map, UltraPerfPayload.DecodeMap(frames[0].Payload.Span));
            Assert.AreEqual(comm, UltraPerfPayload.DecodeComm(frames[1].Payload.Span));
            var readBuildId = UltraPerfPayload.DecodeBuildId(frames[2].Payload.Span);
            Assert.AreEqual(buildId.Path, readBuildId.Path);
            CollectionAssert.AreEqual(buildId.Id, readBuildId.Id);
            Assert.AreEqual("ffffffff81000000 T _text\n", Encoding.UTF8.GetString(frames[3].Payload.Span));
            Assert.AreEqual(UltraPerfFrameKind.PerfRecord, frames[4].Kind);
            Assert.AreEqual((ushort)7, frames[4].Cpu);
            CollectionAssert.AreEqual(record, frames[4].Payload.ToArray());
            Assert.AreEqual(end, UltraPerfPayload.DecodeEnd(frames[5].Payload.Span));
        }
        finally
        {
            file?.Dispose();
            File.Delete(path);
        }
    }

    [TestMethod]
    public void UltraPerfFileReaderStopsAtATruncatedFrame()
    {
        var header = new UltraPerfHeader { CpuCount = 1 };
        var path = Path.Combine(Path.GetTempPath(), $"ultra_test_{Guid.NewGuid():N}.ultraperf");
        try
        {
            using (var writer = new UltraPerfFileWriter(path, header))
            {
                writer.WriteRecord(0, 0, MakeRecord(9, 24, 1));
                writer.WriteRecord(0, 0, MakeRecord(9, 24, 2));
            }

            // Cut the last frame in the middle (the profiler was killed)
            using (var stream = new FileStream(path, FileMode.Open))
            {
                stream.SetLength(stream.Length - 10);
            }

            using (var reader = new UltraPerfFileReader(path))
            {
                Assert.AreEqual(1, reader.ReadFrames().Count());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void CpuListIsParsed()
    {
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, PerfEventCollector.ParseCpuList("0-3\n").ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 8, 9, 10, 11 }, PerfEventCollector.ParseCpuList("0-3,8-11").ToArray());
        CollectionAssert.AreEqual(new[] { 0, 2, 4 }, PerfEventCollector.ParseCpuList("0,2,4").ToArray());
        CollectionAssert.AreEqual(new[] { 7 }, PerfEventCollector.ParseCpuList("7").ToArray());
        Assert.ThrowsExactly<FormatException>(() => PerfEventCollector.ParseCpuList("3-1"));
        Assert.ThrowsExactly<FormatException>(() => PerfEventCollector.ParseCpuList("x"));
    }

    [TestMethod]
    public void MapsLineIsParsed()
    {
        Assert.IsTrue(ProcSnapshot.TryParseExecutableMapsLine(42, "7f0a1c000000-7f0a1c021000 r-xp 00001000 08:01 1234                       /usr/lib/x86_64-linux-gnu/libc.so.6", out var map));
        Assert.AreEqual(new UltraPerfPayload.Map(42, 0x7f0a1c000000, 0x7f0a1c021000, 0x1000, "/usr/lib/x86_64-linux-gnu/libc.so.6"), map);

        // The .NET double mapped JIT code has a pseudo path
        Assert.IsTrue(ProcSnapshot.TryParseExecutableMapsLine(42, "763815e00000-763815f00000 r-xs 00000000 00:01 1038 /memfd:doublemapper (deleted)", out map));
        Assert.AreEqual("/memfd:doublemapper (deleted)", map.Path);

        // Not executable
        Assert.IsFalse(ProcSnapshot.TryParseExecutableMapsLine(42, "7f0a1c021000-7f0a1c1a0000 rw-p 00021000 08:01 1234 /usr/lib/x86_64-linux-gnu/libc.so.6", out _));

        // Anonymous executable mapping without a path
        Assert.IsTrue(ProcSnapshot.TryParseExecutableMapsLine(42, "7f0a1c021000-7f0a1c1a0000 rwxp 00000000 00:00 0", out map));
        Assert.AreEqual(string.Empty, map.Path);
    }

    [TestMethod]
    public void ElfBuildIdIsReadFromThePtNoteSegment()
    {
        // A minimal ELF64 LE image: header (64), one PT_NOTE program header (56), one NT_GNU_BUILD_ID note
        var buildId = new byte[] { 0xde, 0xad, 0xbe, 0xef, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var note = new byte[12 + 4 + 20];
        BinaryPrimitives.WriteUInt32LittleEndian(note, 4); // namesz "GNU\0"
        BinaryPrimitives.WriteUInt32LittleEndian(note.AsSpan(4), 20); // descsz
        BinaryPrimitives.WriteUInt32LittleEndian(note.AsSpan(8), 3); // NT_GNU_BUILD_ID
        "GNU\0"u8.CopyTo(note.AsSpan(12));
        buildId.CopyTo(note, 16);

        var image = new byte[64 + 56 + note.Length];
        image[0] = 0x7f; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F'; image[4] = 2; image[5] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(32), 64); // e_phoff
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(54), 56); // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(56), 1); // e_phnum
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(64), 4); // PT_NOTE
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(64 + 8), 120); // p_offset
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(64 + 32), (ulong)note.Length); // p_filesz
        note.CopyTo(image, 120);

        var read = ProcSnapshot.TryReadElfBuildId(new MemoryStream(image));
        Assert.IsNotNull(read);
        CollectionAssert.AreEqual(buildId, read);

        Assert.IsNull(ProcSnapshot.TryReadElfBuildId(new MemoryStream("MZ not an elf"u8.ToArray())));
    }

    private static byte[] MakeRecord(uint type, ushort size, byte fill)
    {
        var record = new byte[size];
        record.AsSpan(8).Fill(fill);
        BinaryPrimitives.WriteUInt32LittleEndian(record, type);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), size);
        return record;
    }
}
