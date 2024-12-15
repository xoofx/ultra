// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XenoAtom.Collections;

namespace Ultra.Sampler.MacOS;

internal unsafe class MacOSUltraSampler : UltraSampler
{
    private bool _stopped;
    private Thread? _thread;
    private bool _captureEnabled;
    private readonly AutoResetEvent _resumeCaptureThread;

    private const int MaximumFrames = 4096;
    private readonly ulong[] _frames = new ulong[MaximumFrames];

    private const int DefaultImageCount = 1024;
    private UnsafeList<NativeModuleEvent> _moduleEvents = new(DefaultImageCount);
    private bool _initializingModules;
    private readonly object _moduleEventLock = new();
    private int _nextModuleEventIndexToLog;
    private readonly UltraSamplerSource _samplerEventSource;

    private readonly MacOSLibSystem.dyld_register_callback _callbackDyldAdded;
    private readonly MacOSLibSystem.dyld_register_callback _callbackDyldRemoved;

    public MacOSUltraSampler()
    {
        _resumeCaptureThread = new AutoResetEvent(false);

        _callbackDyldAdded = new MacOSLibSystem.dyld_register_callback(CallbackDyldAdded);
        _callbackDyldRemoved = new MacOSLibSystem.dyld_register_callback(CallbackDyldRemoved);

        // Register dyld callbacks
        _initializingModules = true;
        MacOSLibSystem._dyld_register_func_for_add_image(Marshal.GetFunctionPointerForDelegate(_callbackDyldAdded));
        _initializingModules = false;
        MacOSLibSystem._dyld_register_func_for_remove_image(Marshal.GetFunctionPointerForDelegate(_callbackDyldRemoved));

        // Make sure to use the instance to trigger the constructor of the EventSource so that it is registered in the runtime!
        _samplerEventSource = UltraSamplerSource.Log;
    }

    protected override void StartImpl()
    {
        if (_thread is not null) return;

        _thread = new Thread(RunImpl)
        {
            IsBackground = true,
            Name = "Ultra-Sampler",
            Priority = ThreadPriority.Highest
        };
        _thread.Start();
    }

    protected override void StopImpl()
    {
        if (_thread is null) return;

        _resumeCaptureThread.Set();
        _thread.Join();
        _thread = null;
        _stopped = false;
    }

    protected override void EnableImpl()
    {
        _nextModuleEventIndexToLog = 0;
        _captureEnabled = true;
        _resumeCaptureThread.Set();
    }

    protected override void DisableImpl()
    {
        _nextModuleEventIndexToLog = 0;
        _captureEnabled = false;
    }

    private unsafe void RunImpl()
    {
        try
        {
            MacOS.MacOSLibSystem.task_for_pid(MacOS.MacOSLibSystem.mach_task_self(), Process.GetCurrentProcess().Id, out var rootTask)
                .ThrowIfError("task_for_pid");

            MacOS.MacOSLibSystem.pthread_threadid_np(0, out var currentThreadId)
                .ThrowIfError("pthread_threadid_np");

            while (!_stopped)
            {
                if (_captureEnabled)
                {
                    // Load all pending native module events before sampling
                    NotifyPendingNativeModuleEvents();

                    // Sample the callstacks
                    Sample(rootTask, currentThreadId, _frames, UltraSamplerSource.Log.OnNativeCallstack);

                    // Sleep for 1ms
                    Thread.Sleep(1);
                }
                else
                {
                    _resumeCaptureThread.WaitOne();
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Ultra-Sampler unexpected exception while sampling: {ex}");
        }
    }

    private void CallbackDyldAdded(MacOSLibSystem.mach_header* header, nint slideVmAddr)
    {
        AddModuleEvent(_initializingModules ? NativeModuleEventKind.AlreadyLoaded : NativeModuleEventKind.Loaded, (nint)header);
    }

    private void CallbackDyldRemoved(MacOSLibSystem.mach_header* header, IntPtr vmaddr_slide)
    {
        AddModuleEvent(NativeModuleEventKind.Unloaded, (nint)header);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddModuleEvent(NativeModuleEventKind kind, nint loadAddress)
    {
        var result = MacOSLibSystem.dladdr(loadAddress, out var info);
        if (result == 0)
        {
            return;
        }

        NativeModuleEvent evt;
        Unsafe.SkipInit(out evt);
        evt.Kind = kind;
        evt.LoadAddress = (ulong)loadAddress;
        var path = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)info.dli_fname);
        evt.Path = path.ToArray();
        evt.TimestampUtc = DateTime.UtcNow;
        evt.Size = GetMaximumCodeAddress(loadAddress);

        lock (_moduleEventLock)
        {
            _moduleEvents.AddByRef(evt);
        }
    }

    public NativeModuleEvent[] GetNativeModuleEvents()
    {
        lock (_moduleEventLock)
        {
            return _moduleEvents.AsSpan().ToArray();
        }
    }

    private void NotifyPendingNativeModuleEvents()
    {
        lock (_moduleEventLock)
        {
            var events = _moduleEvents.AsSpan();
            for(; _nextModuleEventIndexToLog < events.Length; _nextModuleEventIndexToLog++)
            {
                var evt = events[_nextModuleEventIndexToLog];
                UltraSamplerSource.Log.OnNativeModuleEvent((int)evt.Kind, evt.LoadAddress, evt.Size, evt.TimestampUtc, evt.Path?.Length ?? 0, evt.Path);
            }
        }
    }

    private static bool TryGetUuidFromMacHeader(nint headerPtr, out Guid guid)
    {
        guid = default;
        var header = (MacOSLibSystem.mach_header_64*)headerPtr;
        if (header->magic != MacOSLibSystem.MH_MAGIC_64) throw new InvalidOperationException("Invalid magic header");

        var nbCommands = header->ncmds;
        var commands = (MacOSLibSystem.load_command*)((byte*)header + sizeof(MacOSLibSystem.mach_header_64));
        for(uint i = 0; i < nbCommands; i++)
        {
            var command = commands[i];
            if (command.cmd == MacOSLibSystem.LC_UUID)
            {
                var uuidCommand = (MacOSLibSystem.uuid_command*)Unsafe.AsPointer(ref command);
                guid = uuidCommand->uuid;
                return true;
            }
        }

        return false;
    }

    private static ulong GetMaximumCodeAddress(nint headerPtr)
    {
        ulong startAddress = 0;

        ulong size = 0;
        var header = (MacOSLibSystem.mach_header_64*)headerPtr;
        if (header->magic != MacOSLibSystem.MH_MAGIC_64) throw new InvalidOperationException("Invalid magic header");

        var nbCommands = header->ncmds;
        var commands = (MacOSLibSystem.load_command*)((byte*)header + sizeof(MacOSLibSystem.mach_header_64));
        for(uint i = 0; i < nbCommands; i++)
        {
            ref var command = ref commands[i];
            if (command.cmd == MacOSLibSystem.LC_SEGMENT_64)
            {
                ref var segment = ref Unsafe.As<MacOSLibSystem.load_command,MacOSLibSystem.segment_command_64>(ref command);
                if (segment.vmaddr != 0)
                {
                    if (startAddress == 0)
                    {
                        startAddress = segment.vmaddr;
                    }

                    var newSize = (ulong)((long)segment.vmaddr + (long)segment.vmsize - (long)startAddress);
                    if (newSize > size)
                    {
                        size = newSize;
                    }
                }
            }
        }

        return size;
    }

    public void Sample(NativeCallstackDelegate nativeCallstack)
    {
        MacOS.MacOSLibSystem.task_for_pid(MacOS.MacOSLibSystem.mach_task_self(), Process.GetCurrentProcess().Id, out var rootTask)
            .ThrowIfError("task_for_pid");

        MacOS.MacOSLibSystem.pthread_threadid_np(0, out var currentThreadId)
            .ThrowIfError("pthread_threadid_np");

        Sample(rootTask, currentThreadId, _frames, nativeCallstack);
    }

    private static unsafe void Sample(MacOS.MacOSLibSystem.mach_port_t rootTask, ulong currentThreadId, Span<ulong> frames, NativeCallstackDelegate nativeCallstack)
    {
        // We support only ARM64 for the sampler
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;

        MacOS.MacOSLibSystem.mach_port_t* taskList;
        MacOS.MacOSLibSystem.task_threads(rootTask, &taskList, out uint taskCount)
            .ThrowIfError("task_threads");

        MacOS.MacOSLibSystem.thread_identifier_info threadInfo = new();

        ulong* pFrames = (ulong*)Unsafe.AsPointer(ref frames[0]);

        for (var i = 0; i < taskCount; i++)
        {
            var threadPort = taskList[i];
            try
            {
                int threadInfoCount = MacOS.MacOSLibSystem.THREAD_IDENTIFIER_INFO_COUNT;
                MacOS.MacOSLibSystem.thread_info(threadPort, MacOS.MacOSLibSystem.THREAD_IDENTIFIER_INFO, out threadInfo, ref threadInfoCount)
                    .ThrowIfError("thread_info");

                //var thread_t = pthread_from_mach_thread_np(threadPort);

                //if (thread_t != 0)
                //{
                //    pthread_getname_np(thread_t, nameBuffer, 256)
                //        .ThrowIfError("pthread_getname_np");
                //    Console.WriteLine($"Thread ID: {threadInfo.thread_id} Name: {Marshal.PtrToStringAnsi((IntPtr)nameBuffer)}");
                //}
                //else
                //{
                //    Console.WriteLine($"Thread ID: {threadInfo.thread_id}");
                //}

                if (threadInfo.thread_id == currentThreadId) continue;

                MacOS.MacOSLibSystem.thread_suspend(taskList[i])
                    .ThrowIfError("thread_suspend");

                try
                {
                    MacOS.MacOSLibSystem.arm_thread_state64_t armThreadState = new MacOS.MacOSLibSystem.arm_thread_state64_t();
                    int armThreadStateCount = MacOS.MacOSLibSystem.ARM_THREAD_STATE64_COUNT;

                    MacOS.MacOSLibSystem.thread_get_state(threadPort, MacOS.MacOSLibSystem.ARM_THREAD_STATE64, (nint)(void*)&armThreadState, ref armThreadStateCount)
                        .ThrowIfError("thread_get_state");

                    //Console.WriteLine($"sp: 0x{armThreadState.__sp:X8}, fp: 0x{armThreadState.__fp:X8}, lr: 0x{armThreadState.__lr:X8}");
                    int frameCount = WalkNativeCallStack(armThreadState.__sp, armThreadState.__fp, armThreadState.__lr, pFrames);
                    nativeCallstack(threadInfo.thread_id, frameCount, (byte*)pFrames);
                }
                finally
                {
                    MacOS.MacOSLibSystem.thread_resume(threadPort)
                        .ThrowIfError("thread_resume");
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Ultra-Sampler unexpected exception while sampling thread #{threadInfo.thread_id}: {ex}");
            }
        }
    }

    private static unsafe int WalkNativeCallStack(ulong sp, ulong fp, ulong lr, ulong* frames)
    {
        // The macOS ARM64 mandates that the frame pointer is always present
        int frameIndex = 0;
        while (fp != 0 && frameIndex < MaximumFrames)
        {
            frames[frameIndex++] = lr;
            //sp = fp + 16;
            lr = *(ulong*)(fp + 8);
            fp = *(ulong*)fp;
        }

        return frameIndex;
    }
}