// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Ultra.Core.Linux;

/// <summary>
/// Receives one complete perf record (header included) read from a ring buffer.
/// </summary>
internal delegate void PerfRecordHandler(ReadOnlySpan<byte> record);

/// <summary>
/// Consumes the records of a perf ring buffer data area (a power-of-two sized circular buffer).
/// Separated from the mmap so that the wrap-around handling can be tested without a kernel.
/// </summary>
internal static unsafe class PerfRingReader
{
    /// <summary>The largest record the kernel can write (header size is 16 bits).</summary>
    public const int MaxRecordSize = ushort.MaxValue;

    /// <summary>
    /// Calls <paramref name="handler"/> for each complete record between <paramref name="tail"/> and <paramref name="head"/>
    /// (byte positions that only grow; the data offset is the position masked by the size). Returns the new tail.
    /// </summary>
    /// <param name="data">The start of the data area.</param>
    /// <param name="dataSize">The size of the data area, a power of two.</param>
    /// <param name="tail">The position of the first unread byte.</param>
    /// <param name="head">The position after the last byte written by the kernel.</param>
    /// <param name="scratch">A buffer of at least <see cref="MaxRecordSize"/> bytes for records that wrap around the end of the data area.</param>
    /// <param name="handler">Receives each record.</param>
    public static ulong Drain(byte* data, ulong dataSize, ulong tail, ulong head, Span<byte> scratch, PerfRecordHandler handler)
    {
        ulong mask = dataSize - 1;
        while (tail < head)
        {
            ulong start = tail & mask;

            // The 8-byte header can itself wrap
            ushort size;
            if (start + 8 <= dataSize)
            {
                size = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(data + start + 6, 2));
            }
            else
            {
                for (ulong i = 0; i < 8; i++) scratch[(int)i] = data[(start + i) & mask];
                size = BinaryPrimitives.ReadUInt16LittleEndian(scratch.Slice(6, 2));
            }

            if (size < 8 || tail + size > head)
            {
                // Corrupted or partially written record: stop here, the next drain will see it complete
                break;
            }

            if (start + size <= dataSize)
            {
                handler(new ReadOnlySpan<byte>(data + start, size));
            }
            else
            {
                int first = (int)(dataSize - start);
                new ReadOnlySpan<byte>(data + start, first).CopyTo(scratch);
                new ReadOnlySpan<byte>(data, size - first).CopyTo(scratch.Slice(first));
                handler(scratch.Slice(0, size));
            }

            tail += size;
        }

        return tail;
    }
}

/// <summary>
/// One perf event file descriptor with its mmap'ed ring buffer (one per CPU and per event).
/// </summary>
internal sealed unsafe class PerfEventBuffer : IDisposable
{
    private readonly byte* _pages;
    private readonly nuint _mapLength;
    private readonly ulong _dataOffset;
    private readonly ulong _dataSize;

    public int Fd { get; private set; }

    public int Cpu { get; }

    private PerfEventBuffer(int fd, int cpu, byte* pages, nuint mapLength, ulong dataOffset, ulong dataSize)
    {
        Fd = fd;
        Cpu = cpu;
        _pages = pages;
        _mapLength = mapLength;
        _dataOffset = dataOffset;
        _dataSize = dataSize;
    }

    /// <summary>
    /// Opens a system-wide event on one CPU and maps its ring buffer.
    /// </summary>
    /// <param name="attr">The event attributes.</param>
    /// <param name="cpu">The CPU to sample.</param>
    /// <param name="dataPages">The number of data pages (a power of two) of the ring buffer.</param>
    /// <exception cref="PerfEventException">Thrown when the event cannot be opened or mapped.</exception>
    public static PerfEventBuffer Open(ref PerfEventAttr attr, int cpu, int dataPages)
    {
        if (dataPages <= 0 || (dataPages & (dataPages - 1)) != 0) throw new ArgumentException("dataPages must be a power of two", nameof(dataPages));

        int fd = PerfEventNative.PerfEventOpen(ref attr, -1, cpu);
        if (fd < 0)
        {
            throw new PerfEventException(Marshal.GetLastPInvokeError(), $"perf_event_open failed for cpu {cpu}");
        }

        // One metadata page then the data pages, in the system's page size (16 KB or 64 KB on some arm64 kernels)
        var pageSize = (ulong)Environment.SystemPageSize;
        var mapLength = (nuint)((1 + (ulong)dataPages) * pageSize);
        var pages = PerfEventNative.mmap(null, mapLength, PerfEventNative.PROT_READ | PerfEventNative.PROT_WRITE, PerfEventNative.MAP_SHARED, fd, 0);
        if ((long)pages == -1)
        {
            int errno = Marshal.GetLastPInvokeError();
            PerfEventNative.close(fd);
            throw new PerfEventException(errno, $"mmap of the perf ring buffer failed for cpu {cpu}");
        }

        // The kernel says where the data area is (since 4.1; zero on older kernels: right after the metadata page)
        var dataOffset = *(ulong*)((byte*)pages + PerfEventNative.DataOffsetOffset);
        var dataSize = *(ulong*)((byte*)pages + PerfEventNative.DataSizeOffset);
        if (dataOffset == 0 || dataSize == 0)
        {
            dataOffset = pageSize;
            dataSize = (ulong)dataPages * pageSize;
        }

        return new PerfEventBuffer(fd, cpu, (byte*)pages, mapLength, dataOffset, dataSize);
    }

    public void Enable() => PerfEventNative.ioctl(Fd, PerfEventNative.PERF_EVENT_IOC_ENABLE, 0);

    public void Disable() => PerfEventNative.ioctl(Fd, PerfEventNative.PERF_EVENT_IOC_DISABLE, 0);

    /// <summary>
    /// Passes all complete records to the handler and releases them to the kernel.
    /// </summary>
    public void Drain(Span<byte> scratch, PerfRecordHandler handler)
    {
        ulong head = Volatile.Read(ref *(ulong*)(_pages + PerfEventNative.DataHeadOffset));
        ulong tail = *(ulong*)(_pages + PerfEventNative.DataTailOffset);
        if (tail == head) return;

        tail = PerfRingReader.Drain(_pages + _dataOffset, _dataSize, tail, head, scratch, handler);

        // Release store: the kernel may overwrite the consumed bytes once it sees the new tail
        Volatile.Write(ref *(ulong*)(_pages + PerfEventNative.DataTailOffset), tail);
    }

    public void Dispose()
    {
        if (Fd < 0) return;
        PerfEventNative.munmap(_pages, _mapLength);
        PerfEventNative.close(Fd);
        Fd = -1;
    }
}

/// <summary>
/// A perf_event_open or ring buffer error with its errno.
/// </summary>
internal sealed class PerfEventException(int errno, string message) : InvalidOperationException($"{message} (errno {errno})")
{
    public int Errno { get; } = errno;
}
