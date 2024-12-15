// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Ultra.Sampler.MacOS.MacOSLibSystem;
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value

namespace Ultra.Sampler.MacOS;

using task_inspect_t = mach_port_t;
using thread_act_t = mach_port_t;
using thread_inspect_t = mach_port_t;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static partial class MacOSLibSystem
{
    public const int EPERM = 1;

    public const int KERN_INVALID_ADDRESS = 1;

    public const int PROT_READ = 0x01;
    public const int PROT_WRITE = 0x02;
    public const int PROT_EXEC = 0x04;

    public const int PT_ATTACH = 10; // TODO: deprecated
    public const int PT_DETACH = 11;

    public const int TASK_DYLD_INFO = 17;
    public const int THREAD_IDENTIFIER_INFO = 4;
    public const int x86_THREAD_STATE64 = 4;
    public const int ARM_THREAD_STATE64 = 6;
    public const int VM_REGION_BASIC_INFO_64 = 9;

    public static readonly unsafe int TASK_DYLD_INFO_COUNT = sizeof(task_dyld_info) / sizeof(uint);

    public static readonly unsafe int THREAD_IDENTIFIER_INFO_COUNT = sizeof(thread_identifier_info) / sizeof(uint);

    public static readonly unsafe int x86_THREAD_STATE64_COUNT = sizeof(x86_thread_state64_t) / sizeof(uint);
    public static readonly unsafe int ARM_THREAD_STATE64_COUNT = sizeof(arm_thread_state64_t) / sizeof(uint);

    public static readonly unsafe int VM_REGION_BASIC_INFO_COUNT_64 = sizeof(vm_region_basic_info_64) / sizeof(int);

    private const string LibSystem = "libSystem.dylib";

    [LibraryImport(LibSystem)]
    public static partial mach_port_t mach_task_self();

    [LibraryImport(LibSystem)]
    public static partial return_t task_for_pid(mach_port_t host, pid_t pid, out mach_port_t task);

    [LibraryImport(LibSystem)]
    public static unsafe partial return_t task_info(mach_port_t host, uint flavor, task_dyld_info* task_info, ref /*uint*/int task_info_count);

    [LibraryImport(LibSystem)]
    public static unsafe partial return_t task_threads(task_inspect_t target_task, thread_act_t** act_list, out uint act_list_count);

    [LibraryImport(LibSystem)]
    public static unsafe partial return_t task_suspend(task_inspect_t target_task);

    [LibraryImport(LibSystem)]
    public static unsafe partial return_t task_resume(task_inspect_t target_task);

    [LibraryImport(LibSystem)]
    public static partial return_t thread_suspend(thread_act_t target_act);

    [LibraryImport(LibSystem)]
    public static partial return_t thread_resume(thread_act_t target_act);

    [LibraryImport(LibSystem)]
    public static partial return_t thread_info(thread_act_t target_act, uint flavor, out /*int*/thread_identifier_info thread_info, ref /*uint*/int thread_info_count);

    [LibraryImport(LibSystem)]
    public static partial return_t thread_get_state(thread_inspect_t target_act, uint flavor, /*uint**/nint old_state, ref /*uint*/int old_state_count);

    [LibraryImport(LibSystem)]
    public static partial nint pthread_from_mach_thread_np(thread_act_t target_act);

    [LibraryImport(LibSystem)]
    public static partial return_t pthread_threadid_np(nint thread, out ulong threadId);

    [LibraryImport(LibSystem)]
    public static unsafe partial return_t pthread_getname_np(nint thread, byte* name, nint len);

    [LibraryImport(LibSystem)]
    public static unsafe partial int vm_read_overwrite(int target_task, /*UIntPtr*/ulong address, /*UIntPtr*/
        long size, /*UIntPtr*/void* data, out /*UIntPtr*/long data_size);

    [LibraryImport(LibSystem)]
    public static partial int mach_vm_region(int target_task, ref /*UIntPtr*/ulong address,
        out /*UIntPtr*/ulong size, int flavor, out /*int*/vm_region_basic_info_64 info, ref /*uint*/int info_count,
        out int object_name);

    [LibraryImport(LibSystem)]
    public static partial return_t mach_vm_remap(
        int target_task,
        ref /*UIntPtr*/ulong address,
        /*UIntPtr*/ulong size,
        /*UIntPtr*/ulong mask,
        int flags,
        int src_task,
        /*UIntPtr*/ulong src_address,
        int copy,
        out /*UIntPtr*/ulong new_address);

    [LibraryImport(LibSystem)]
    public static partial int mach_vm_deallocate(int target_task, /*UIntPtr*/ulong address, /*UIntPtr*/ulong size);

    [LibraryImport(LibSystem)]
    public static partial int mach_port_deallocate( /*uint*/ int task, uint name);

    [LibraryImport(LibSystem)]
    public static partial int waitpid(int pid, IntPtr status, int options);

    public unsafe delegate void dyld_register_callback(mach_header* mh, nint vmaddr_slide);

    [LibraryImport(LibSystem)]
    public static partial void _dyld_register_func_for_add_image(nint callback);

    [LibraryImport(LibSystem)]
    public static partial void _dyld_register_func_for_remove_image(nint callback);

    [LibraryImport(LibSystem)]
    public static partial int dladdr(nint address, out dl_info info);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint mach_vm_trunk_page(nint addr) => addr & ~(Environment.SystemPageSize - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint mach_vm_round_page(nint addr) => (addr + Environment.SystemPageSize - 1) & ~(Environment.SystemPageSize - 1);

    public unsafe struct dyld_all_image_infos
    {
        public readonly uint version;
        public readonly uint infoArrayCount;
        public readonly dyld_image_info* infoArray; // Pointer to dyld_image_info
        public readonly nint notification; // Function pointer (delegate can be defined for callback)
        public readonly bool processDetachedFromSharedRegion;
        public readonly bool libSystemInitialized;
        public readonly nint dyldImageLoadAddress; // Pointer to mach_header
        public readonly nint jitInfo; // Pointer to void
        public readonly nint dyldVersion; // Pointer to a null-terminated UTF8 string
        public readonly nint errorMessage; // Pointer to a null-terminated UTF8 string
        public readonly nint terminationFlags; // Equivalent to usize
        public readonly nint coreSymbolicationShmPage; // Pointer to void
        public readonly nint systemOrderFlag; // Equivalent to usize
        public readonly nint uuidArrayCount; // Equivalent to usize
        public readonly nint uuidArray; // Pointer to dyld_uuid_info
        public readonly dyld_all_image_infos* dyldAllImageInfosAddress; // Pointer to dyld_all_image_infos
        public readonly nint initialImageCount; // Equivalent to usize
        public readonly nint errorKind; // Equivalent to usize
        public readonly nint errorClientOfDylibPath; // Pointer to a null-terminated UTF8 string
        public readonly nint errorTargetDylibPath; // Pointer to a null-terminated UTF8 string
        public readonly nint errorSymbol; // Pointer to a null-terminated UTF8 string
        public readonly nint sharedCacheSlide; // Equivalent to usize
        public fixed byte sharedCacheUUID[16]; // Fixed-size array
        public readonly nint sharedCacheBaseAddress; // Equivalent to usize
        public readonly ulong infoArrayChangeTimestamp; // Equivalent to u64
        public readonly nint dyldPath; // Pointer to a null-terminated UTF8 string
        public fixed uint notifyPorts[8]; // Fixed-size array for mach_port_t
        public nint reserved0; // Fixed-size array equivalent to usize
        public nint reserved1; // Fixed-size array equivalent to usize
        public nint reserved2; // Fixed-size array equivalent to usize
        public nint reserved3; // Fixed-size array equivalent to usize
        public nint reserved4; // Fixed-size array equivalent to usize
        public nint reserved5; // Fixed-size array equivalent to usize
        public nint reserved6; // Fixed-size array equivalent to usize
        public readonly ulong sharedCacheFSID; // Equivalent to u64
        public readonly ulong sharedCacheFSObjID; // Equivalent to u64
        public readonly nint compact_dyld_image_info_addr; // Equivalent to usize
        public readonly nuint compact_dyld_image_info_size; // Equivalent to size_t
        public readonly uint platform;
        public readonly uint aotInfoCount;
        public readonly nint aotInfoArray; // Pointer to dyld_aot_image_info
        public readonly ulong aotInfoArrayChangeTimestamp; // Equivalent to u64
        public readonly nint aotSharedCacheBaseAddress; // Equivalent to usize
        public fixed byte aotSharedCacheUUID[16]; // Fixed-size array
        // We don't need the rest of this struct so we do not define the rest of the fields.
    }

    public readonly struct dyld_image_info
    {
        public readonly nint imageLoadAddress; // Pointer to mach_header
        public readonly nint imageFilePath; // Pointer to a null-terminated UTF8 string
        public readonly nint imageFileModDate; // Equivalent to usize
    }

    public readonly unsafe struct task_dyld_info
    {
        public readonly nint all_image_info_addr;
        public readonly nint all_image_info_size;
        public readonly int all_image_info_format;
    }

    public readonly struct thread_identifier_info
    {
        public readonly ulong thread_id;
        public readonly ulong thread_handle;
        public readonly ulong dispatch_qaddr;
    }

    public readonly struct vm_region_basic_info_64
    {
        public readonly int protection;
        public readonly int max_protection;
        public readonly uint inheritance;
        public readonly uint shared;
        public readonly uint reserved;
        public readonly ulong offset;
        public readonly int behavior;
        public readonly ushort user_wired_count;
    }

    public readonly record struct return_t(int Value)
    {
        public bool IsSuccess => Value == 0;

        public bool IsError => Value != 0;

        public void ThrowIfError(string message)
        {
            if (IsError) throw new Exception($"{message} failed with error code {Value}");
        }
    }

    public readonly record struct mach_port_t(int Value);

    public readonly record struct pid_t(int Value)
    {
        public static implicit operator pid_t(int value) => new(value);
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct thread_state_t
    {
        [FieldOffset(0)] public x86_thread_state64_t x64;

        [FieldOffset(0)] public arm_thread_state64_t arm;
    }


    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct arm_thread_state64_t
    {
        public unsafe fixed ulong __x[29];
        public ulong __fp;
        public ulong __lr;
        public ulong __sp;
        public ulong __pc;
        public uint __cpsr;
        public uint __pad; // or __opaque_flags when ptrauth is enabled
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct x86_thread_state64_t
    {
        public readonly ulong __rax;
        public readonly ulong __rbx;
        public readonly ulong __rcx;
        public readonly ulong __rdx;
        public readonly ulong __rdi;
        public readonly ulong __rsi;
        public readonly ulong __rbp;
        public readonly ulong __rsp;
        public readonly ulong __r8;
        public readonly ulong __r9;
        public readonly ulong __r10;
        public readonly ulong __r11;
        public readonly ulong __r12;
        public readonly ulong __r13;
        public readonly ulong __r14;
        public readonly ulong __r15;
        public readonly ulong __rip;
        public readonly ulong __rflags;
        public readonly ulong __cs;
        public readonly ulong __fs;
        public readonly ulong __gs;
    }

    public struct MemoryRegion //: IRegion
    {
        public ulong BeginAddress { get; set; }
        public ulong EndAddress { get; set; }

        public int Permission { get; set; }

        public bool IsReadable => (Permission & PROT_READ) != 0;
    }

    public const uint MH_MAGIC_64 = 0xfeedfacf;
    public const uint MH_MAGIC = 0xfeedface;

/*
 * Structure filled in by dladdr().
 */
    public struct dl_info
    {
        public nint dli_fname; /* Pathname of shared object */
        public nint dli_fbase; /* Base address of shared object */
        public nint dli_sname; /* Name of nearest symbol */
        public nint dli_saddr; /* Address of nearest symbol */
    }

    /*
     * The 32-bit mach header appears at the very beginning of the object file for
     * 32-bit architectures.
     */
    public struct mach_header {
        public uint	magic;		/* mach magic number identifier */
        public int		cputype;	/* cpu specifier */
        public int		cpusubtype;	/* machine specifier */
        public uint	filetype;	/* type of file */
        public uint	ncmds;		/* number of load commands */
        public uint	sizeofcmds;	/* the size of all the load commands */
        public uint	flags;		/* flags */
    };

    /*
     * The 64-bit mach header appears at the very beginning of object files for
     * 64-bit architectures.
     */
    public struct mach_header_64 {
        public uint	magic;		/* mach magic number identifier */
        public int		cputype;	/* cpu specifier */
        public int		cpusubtype;	/* machine specifier */
        public uint	filetype;	/* type of file */
        public uint	ncmds;		/* number of load commands */
        public uint	sizeofcmds;	/* the size of all the load commands */
        public uint	flags;		/* flags */
        public uint	reserved;	/* reserved */
    };

    public struct load_command {
        public uint cmd;		/* type of load command */
        public uint cmdsize;	/* total size of command in bytes */
    };

    /*
     * The uuid load command contains a single 128-bit unique random number that
     * identifies an object produced by the static link editor.
     */
    public struct uuid_command {
        public uint	cmd;		/* LC_UUID */
        public uint	cmdsize;	/* sizeof(struct uuid_command) */
        public Guid	uuid;	  /* the 128-bit uuid */
    }

    public unsafe struct segment_command_64 { /* for 64-bit architectures */
        public uint	cmd;		/* LC_SEGMENT_64 */
        public uint	cmdsize;	/* includes sizeof section_64 structs */
        public fixed byte segname[16];	/* segment name */
        public ulong	vmaddr;		/* memory address of this segment */
        public ulong	vmsize;		/* memory size of this segment */
        public ulong	fileoff;	/* file offset of this segment */
        public ulong	filesize;	/* amount to map from the file */
        public int		maxprot;	/* maximum VM protection */
        public int		initprot;	/* initial VM protection */
        public uint	nsects;		/* number of sections in segment */
        public uint	flags;		/* flags */
    }

    public unsafe struct section_64 { /* for 64-bit architectures */
        public fixed byte		sectname[16];	/* name of this section */
        public fixed byte		segname[16];	/* segment this section goes in */
        public ulong	addr;		/* memory address of this section */
        public ulong	size;		/* size in bytes of this section */
        public uint	offset;		/* file offset of this section */
        public uint	align;		/* section alignment (power of 2) */
        public uint	reloff;		/* file offset of relocation entries */
        public uint	nreloc;		/* number of relocation entries */
        public uint	flags;		/* flags (section type and attributes)*/
        public uint	reserved1;	/* reserved (for offset or index) */
        public uint	reserved2;	/* reserved (for count or sizeof) */
        public uint	reserved3;	/* reserved */
    }

    public const uint LC_UUID = 0x1b;
    public const uint LC_SEGMENT_64 = 0x19;

}
