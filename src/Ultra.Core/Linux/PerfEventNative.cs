// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.InteropServices;

namespace Ultra.Core.Linux;

/// <summary>
/// Constants, structures and libc imports for perf_event_open (Linux only; only called on Linux).
/// </summary>
internal static unsafe class PerfEventNative
{
    // perf_event_attr.type
    public const uint PERF_TYPE_HARDWARE = 0;
    public const uint PERF_TYPE_SOFTWARE = 1;
    public const uint PERF_TYPE_RAW = 4;

    // perf_event_attr.config for PERF_TYPE_SOFTWARE
    public const ulong PERF_COUNT_SW_CPU_CLOCK = 0;

    // perf_event_attr.sample_type
    public const ulong PERF_SAMPLE_IP = 1UL << 0;
    public const ulong PERF_SAMPLE_TID = 1UL << 1;
    public const ulong PERF_SAMPLE_TIME = 1UL << 2;
    public const ulong PERF_SAMPLE_CALLCHAIN = 1UL << 5;
    public const ulong PERF_SAMPLE_CPU = 1UL << 7;
    public const ulong PERF_SAMPLE_PERIOD = 1UL << 8;

    // perf_event_attr bit flags (the bitfield after read_format)
    public const ulong ATTR_DISABLED = 1UL << 0;
    public const ulong ATTR_EXCLUDE_IDLE = 1UL << 7;
    public const ulong ATTR_MMAP = 1UL << 8;
    public const ulong ATTR_COMM = 1UL << 9;
    public const ulong ATTR_FREQ = 1UL << 10;
    public const ulong ATTR_TASK = 1UL << 13;
    public const ulong ATTR_WATERMARK = 1UL << 14;
    public const ulong ATTR_SAMPLE_ID_ALL = 1UL << 18;
    public const ulong ATTR_MMAP2 = 1UL << 23;
    public const ulong ATTR_USE_CLOCKID = 1UL << 25;
    public const ulong ATTR_CONTEXT_SWITCH = 1UL << 26;
    public const ulong ATTR_BUILD_ID = 1UL << 34;

    // perf_event_header.type
    public const uint PERF_RECORD_MMAP = 1;
    public const uint PERF_RECORD_LOST = 2;
    public const uint PERF_RECORD_COMM = 3;
    public const uint PERF_RECORD_EXIT = 4;
    public const uint PERF_RECORD_FORK = 7;
    public const uint PERF_RECORD_SAMPLE = 9;
    public const uint PERF_RECORD_MMAP2 = 10;
    public const uint PERF_RECORD_SWITCH = 14;
    public const uint PERF_RECORD_SWITCH_CPU_WIDE = 15;

    // perf_event_header.misc
    public const ushort PERF_RECORD_MISC_SWITCH_OUT = 0x2000;
    public const ushort PERF_RECORD_MISC_MMAP_BUILD_ID = 0x4000;

    // Call chain context markers
    public const ulong PERF_CONTEXT_KERNEL = unchecked((ulong)-128L);
    public const ulong PERF_CONTEXT_USER = unchecked((ulong)-512L);
    public const ulong PERF_CONTEXT_MAX = unchecked((ulong)-4095L);

    public const int CLOCK_MONOTONIC = 1;
    public const ulong PERF_FLAG_FD_CLOEXEC = 8;
    public const int PROT_READ = 1;
    public const int PROT_WRITE = 2;
    public const int MAP_SHARED = 1;
    public const short POLLIN = 1;
    public const ulong PERF_EVENT_IOC_ENABLE = 0x2400;
    public const ulong PERF_EVENT_IOC_DISABLE = 0x2401;
    public const int EINVAL = 22;
    public const int EACCES = 13;
    public const int EPERM = 1;

    /// <summary>Offsets in the first (metadata) page of the ring buffer mapping (struct perf_event_mmap_page: data_head, data_tail, data_offset, data_size).</summary>
    public const int DataHeadOffset = 1024;
    public const int DataTailOffset = 1032;
    public const int DataOffsetOffset = 1040;
    public const int DataSizeOffset = 1048;

    public static long SysPerfEventOpen => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 298,
        Architecture.Arm64 => 241,
        _ => throw new PlatformNotSupportedException($"perf_event_open syscall number unknown for {RuntimeInformation.ProcessArchitecture}")
    };

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, PerfEventAttr* attr, int pid, int cpu, int groupFd, ulong flags);

    [DllImport("libc", SetLastError = true)]
    public static extern void* mmap(void* addr, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc", SetLastError = true)]
    public static extern int munmap(void* addr, nuint length);

    [DllImport("libc", SetLastError = true)]
    public static extern int ioctl(int fd, ulong request, long arg);

    [DllImport("libc", SetLastError = true)]
    public static extern int poll(PollFd* fds, nuint nfds, int timeoutMs);

    [DllImport("libc", SetLastError = true)]
    public static extern int close(int fd);

    [DllImport("libc")]
    public static extern uint geteuid();

    [DllImport("libc")]
    private static extern int clock_gettime(int clockId, Timespec* ts);

    /// <summary>Opens a perf event. Returns the file descriptor, or -1 with errno in <see cref="Marshal.GetLastPInvokeError"/>.</summary>
    public static int PerfEventOpen(ref PerfEventAttr attr, int pid, int cpu)
    {
        fixed (PerfEventAttr* p = &attr)
        {
            return (int)syscall(SysPerfEventOpen, p, pid, cpu, -1, PERF_FLAG_FD_CLOEXEC);
        }
    }

    /// <summary>CLOCK_MONOTONIC in nanoseconds, the clock of the perf samples (use_clockid) and of the .NET runtime on Linux.</summary>
    public static ulong MonotonicNanoseconds()
    {
        Timespec ts;
        clock_gettime(CLOCK_MONOTONIC, &ts);
        return (ulong)ts.tv_sec * 1_000_000_000UL + (ulong)ts.tv_nsec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long tv_sec;
        public long tv_nsec;
    }
}

/// <summary>
/// struct perf_event_header.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PerfEventHeader
{
    public uint type;
    public ushort misc;
    public ushort size;
}

/// <summary>
/// struct pollfd.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PollFd
{
    public int fd;
    public short events;
    public short revents;
}

/// <summary>
/// struct perf_event_attr (PERF_ATTR_SIZE_VER6, 120 bytes). Stored as-is in the .ultraperf file.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = Size)]
internal struct PerfEventAttr
{
    public const int Size = 120;

    public uint type;
    public uint size;
    public ulong config;
    public ulong sample_period_or_freq;
    public ulong sample_type;
    public ulong read_format;
    public ulong flags;
    public uint wakeup_events_or_watermark;
    public uint bp_type;
    public ulong bp_addr_or_config1;
    public ulong bp_len_or_config2;
    public ulong branch_sample_type;
    public ulong sample_regs_user;
    public uint sample_stack_user;
    public int clockid;
    public ulong sample_regs_intr;
    public uint aux_watermark;
    public ushort sample_max_stack;
    public ushort reserved2;
    public uint aux_sample_size;
    public uint reserved3;
}
