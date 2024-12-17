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
    // Sampler General info/state
    private bool _samplerStopped;
    private bool _samplerEnabled;
    private Thread? _samplerThread;
    private ulong _samplerThreadId;
    private readonly AutoResetEvent _samplerResumeThreadEvent;

    // Frames information
    private const int MaximumFrames = 4096;
    private readonly ulong[] _frames; // 32 KB

    private const int MaximumCompressedFrameTotalCount = 64;
    private const int MaximumCompressedFrameCount = MaximumCompressedFrameTotalCount - 1;
    private const int MaximumThreadCountForCompressedFrames = 512;
    private readonly ulong[] _allCompressedFrames; // 256 KB
    private UnsafeList<int> _freeCompressedFramesIndices = new(MaximumThreadCountForCompressedFrames);
    private UnsafeDictionary<ulong, int> _threadIdToCompressedFrameIndex = new(MaximumThreadCountForCompressedFrames);
    private UnsafeHashSet<ulong> _activeThreadIds = new(MaximumThreadCountForCompressedFrames);
    private UnsafeHashSet<ulong> _currentThreadIds = new(MaximumThreadCountForCompressedFrames);

    // Modules
    private const int DefaultImageCount = 1024;
    private UnsafeList<NativeModuleEvent> _moduleEvents = new(DefaultImageCount);
    private readonly bool _initializingModules;
    private readonly object _moduleEventLock = new();
    private int _nextModuleEventIndexToLog;
    private readonly UltraSamplerSource _samplerEventSource;
    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly MacOSLibSystem.dyld_register_callback _callbackDyldAdded; // Keep a reference to avoid the GC to collect it
    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly MacOSLibSystem.dyld_register_callback _callbackDyldRemoved; // Keep a reference to avoid the GC to collect it

    public MacOSUltraSampler()
    {
        // Make sure to use the instance to trigger the constructor of the EventSource so that it is registered in the runtime!
        _samplerEventSource = UltraSamplerSource.Log;

        _frames = GC.AllocateArray<ulong>(4096, true);
        _allCompressedFrames = GC.AllocateArray<ulong>(MaximumCompressedFrameTotalCount * MaximumThreadCountForCompressedFrames, true);

        // Initialize _freeCompressedFramesIndices (order from last to first to pickup the index 0 first)
        for (int i = MaximumThreadCountForCompressedFrames - 1; i >= 0; --i)
        {
            _freeCompressedFramesIndices.Add(i);
        }

        _samplerResumeThreadEvent = new AutoResetEvent(false);

        _callbackDyldAdded = new MacOSLibSystem.dyld_register_callback(CallbackDyldAdded);
        _callbackDyldRemoved = new MacOSLibSystem.dyld_register_callback(CallbackDyldRemoved);
        
        // Register dyld callbacks
        _initializingModules = true;
        MacOSLibSystem._dyld_register_func_for_add_image(Marshal.GetFunctionPointerForDelegate(_callbackDyldAdded));
        _initializingModules = false;
        MacOSLibSystem._dyld_register_func_for_remove_image(Marshal.GetFunctionPointerForDelegate(_callbackDyldRemoved));
    }

    protected override void StartImpl()
    {
        if (_samplerThread is not null) return;

        _samplerThread = new Thread(RunImpl)
        {
            IsBackground = true,
            Name = "Ultra-Sampler",
            Priority = ThreadPriority.Highest
        };
        _samplerThread.Start();
    }

    protected override void StopImpl()
    {
        if (_samplerThread is null) return;

        _samplerResumeThreadEvent.Set();
        _samplerThread.Join();
        _samplerThread = null;
        _samplerStopped = false;
    }

    protected override void EnableImpl()
    {
        _nextModuleEventIndexToLog = 0;
        _samplerEnabled = true;
        _samplerResumeThreadEvent.Set();
    }

    protected override void DisableImpl()
    {
        _nextModuleEventIndexToLog = 0;
        _samplerEnabled = false;
    }

    private unsafe void RunImpl()
    {
        try
        {
            MacOS.MacOSLibSystem.task_for_pid(MacOS.MacOSLibSystem.mach_task_self(), Process.GetCurrentProcess().Id, out var rootTask)
                .ThrowIfError("task_for_pid");

            MacOS.MacOSLibSystem.pthread_threadid_np(0, out _samplerThreadId)
                .ThrowIfError("pthread_threadid_np");

            bool sendManifest = true;

            while (!_samplerStopped)
            {
                if (_samplerEnabled)
                {
                    if (sendManifest)
                    {
                        SendManifest();
                        sendManifest = false;
                    }

                    // Load all pending native module events before sampling
                    NotifyPendingNativeModuleEvents();

                    // Sample the callstacks
                    Sample(rootTask, UltraSamplerSource.Log.NativeCallstack);

                    // Sleep for 1ms
                    Thread.Sleep(1);
                }
                else
                {
                    ClearThreadStates();
                    _samplerResumeThreadEvent.WaitOne();
                    sendManifest = true;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ultra-Sampler unexpected exception while sampling: {ex}");
        }
    }


    private void ClearThreadStates()
    {
        // Reset the state for threads
        _currentThreadIds.Clear();
        _activeThreadIds.Clear();
        foreach (var compressedIndex in _threadIdToCompressedFrameIndex.Values)
        {
            _freeCompressedFramesIndices.Add(compressedIndex);
        }
        _threadIdToCompressedFrameIndex.Clear();
    }

    private static void SendManifest()
    {
        // Make sure to always send the manifest before resuming the capture thread
        try
        {
            // TODO: Doesn't seem to work on macOS (only valid for ETW?)
            EventSource.SendCommand(UltraSamplerSource.Log, EventCommand.SendManifest, null);
        }
        catch
        {
            // Ignore
        }
    }

    private void CallbackDyldAdded(MacOSLibSystem.mach_header* header, nint slideVmAddr)
    {
        AddModuleEvent(_initializingModules ? UltraSamplerNativeModuleEventKind.AlreadyLoaded : UltraSamplerNativeModuleEventKind.Loaded, (nint)header);
    }

    private void CallbackDyldRemoved(MacOSLibSystem.mach_header* header, IntPtr vmaddr_slide)
    {
        AddModuleEvent(UltraSamplerNativeModuleEventKind.Unloaded, (nint)header);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddModuleEvent(UltraSamplerNativeModuleEventKind kind, nint loadAddress)
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
        evt.Size = GetDyldCodeSize(loadAddress);
        TryGetUuidFromMacHeader(loadAddress, out evt.Uuid);

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
                UltraSamplerSource.Log.NativeModuleEvent((int)evt.Kind, evt.LoadAddress, evt.Size, evt.TimestampUtc, evt.Uuid, evt.Path);
            }
        }
    }

    private static bool TryGetUuidFromMacHeader(nint headerPtr, out Guid guid)
    {
        guid = default;
        var header = (MacOSLibSystem.mach_header_64*)headerPtr;
        if (header->magic != MacOSLibSystem.MH_MAGIC_64) throw new InvalidOperationException("Invalid magic header");

        var nbCommands = header->ncmds;
        ref var firstCommand = ref *(MacOSLibSystem.load_command*)((byte*)header + sizeof(MacOSLibSystem.mach_header_64));
        ref var command = ref firstCommand;
        for (uint i = 0; i < nbCommands; i++)
        {
            if (command.cmd == MacOSLibSystem.LC_UUID)
            {
                ref var uuidCommand = ref Unsafe.As<MacOSLibSystem.load_command, MacOSLibSystem.uuid_command>(ref command);
                guid = uuidCommand.uuid;
                return true;
            }
            command = ref Unsafe.AddByteOffset(ref command, command.cmdsize);
        }

        return false;
    }

    private static ulong GetDyldCodeSize(nint headerPtr)
    {
        ulong startAddress = ulong.MaxValue;

        ulong size = 0;
        var header = (MacOSLibSystem.mach_header_64*)headerPtr;
        if (header->magic != MacOSLibSystem.MH_MAGIC_64) throw new InvalidOperationException("Invalid magic header");

        var nbCommands = header->ncmds;
        ref var firstCommand = ref *(MacOSLibSystem.load_command*)((byte*)header + sizeof(MacOSLibSystem.mach_header_64));
        ref var command = ref firstCommand;
        for (uint i = 0; i < nbCommands; i++)
        {
            if (command.cmd == MacOSLibSystem.LC_SEGMENT_64)
            {
                ref var segment = ref Unsafe.As<MacOSLibSystem.load_command,MacOSLibSystem.segment_command_64>(ref command);
                if ((segment.initprot & MacOSLibSystem.PROT_EXEC) != 0  && segment.vmaddr < startAddress)
                {
                    startAddress = segment.vmaddr;
                }
            }
            command = ref Unsafe.AddByteOffset(ref command, command.cmdsize);
        }

        if (startAddress == ulong.MaxValue) return 0;
        
        command = ref firstCommand;
        for (uint i = 0; i < nbCommands; i++)
        {
            if (command.cmd == MacOSLibSystem.LC_SEGMENT_64)
            {
                ref var segment = ref Unsafe.As<MacOSLibSystem.load_command, MacOSLibSystem.segment_command_64>(ref command);

                var newSize = (ulong)((long)segment.vmaddr + (long)segment.vmsize - (long)startAddress);
                if ((segment.initprot & MacOSLibSystem.PROT_EXEC) != 0 && newSize > size)
                {
                    size = newSize;
                }
            }
            command = ref Unsafe.AddByteOffset(ref command, command.cmdsize);
        }

        return size;
    }

    //public void Sample(NativeCallstackDelegate nativeCallstack)
    //{
    //    MacOS.MacOSLibSystem.task_for_pid(MacOS.MacOSLibSystem.mach_task_self(), Process.GetCurrentProcess().Id, out var rootTask)
    //        .ThrowIfError("task_for_pid");

    //    MacOS.MacOSLibSystem.pthread_threadid_np(0, out var currentThreadId)
    //        .ThrowIfError("pthread_threadid_np");

    //    Sample(rootTask, currentThreadId, _frames, nativeCallstack);
    //}

    private unsafe void Sample(MacOS.MacOSLibSystem.mach_port_t rootTask, NativeSamplingDelegate samplingDelegate)
    {
        // We support only ARM64 for the sampler
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;

        MacOS.MacOSLibSystem.thread_identifier_info threadInfo = default;
        MacOSLibSystem.thread_extended_info threadExtendedInfo = default;
        MacOS.MacOSLibSystem.arm_thread_state64_t armThreadState = new MacOS.MacOSLibSystem.arm_thread_state64_t();
        int armThreadStateCount = MacOS.MacOSLibSystem.ARM_THREAD_STATE64_COUNT;

        MacOS.MacOSLibSystem.mach_port_t* taskList;
        MacOS.MacOSLibSystem.task_threads(rootTask, &taskList, out uint taskCount)
            .ThrowIfError("task_threads");

        ulong* pFrames = (ulong*)Unsafe.AsPointer(ref _frames[0]);

        _currentThreadIds.Clear();
        for (var i = 0; i < taskCount; i++)
        {
            var threadPort = taskList[i];

            int infoCount = MacOS.MacOSLibSystem.THREAD_IDENTIFIER_INFO_COUNT;
            var status = MacOS.MacOSLibSystem.thread_info(threadPort, MacOS.MacOSLibSystem.THREAD_IDENTIFIER_INFO, &threadInfo, ref infoCount);
            if (status.IsError)
            {
                continue;
            }

            if (threadInfo.thread_id == _samplerThreadId) continue;

            _currentThreadIds.Add(threadInfo.thread_id); // Record which thread was seen/active

            infoCount = MacOS.MacOSLibSystem.THREAD_EXTENDED_INFO_COUNT;
            status = MacOS.MacOSLibSystem.thread_info(threadPort, MacOS.MacOSLibSystem.THREAD_EXTENDED_INFO, &threadExtendedInfo, ref infoCount);
            if (status.IsError || (threadExtendedInfo.pth_flags & MacOSLibSystem.TH_FLAGS_IDLE) != 0) // If the thread is idle, we skip it
            {
                continue;
            }

            // -------------------------------------------------------------------
            // Suspend the thread
            // -------------------------------------------------------------------
            status = MacOS.MacOSLibSystem.thread_suspend(taskList[i]);
            if (status.IsError) // Don't throw if we can't suspend a thread
            {
                continue;
            }

            int frameCount = 0;

            // -------------------------------------------------------------------
            // Resume the thread
            // -------------------------------------------------------------------
            status = MacOS.MacOSLibSystem.thread_get_state(threadPort, MacOS.MacOSLibSystem.ARM_THREAD_STATE64, (nint)(void*)&armThreadState, ref armThreadStateCount);
            if (status.IsSuccess)
            {
                //Console.WriteLine($"sp: 0x{armThreadState.__sp:X8}, fp: 0x{armThreadState.__fp:X8}, lr: 0x{armThreadState.__lr:X8}");
                frameCount = WalkNativeCallStack(armThreadState.__sp, armThreadState.__fp, armThreadState.__lr, pFrames);
            }

            // -------------------------------------------------------------------
            // Resume the thread
            // -------------------------------------------------------------------
            MacOS.MacOSLibSystem.thread_resume(threadPort);

            // Compute the same frame count
            var sameFrameCount = ComputeSameFrameCount(threadInfo.thread_id, frameCount, pFrames);
            frameCount -= sameFrameCount;

            // Long only the delta frames
            samplingDelegate(threadInfo.thread_id, (int)threadExtendedInfo.pth_run_state, (int)threadExtendedInfo.pth_cpu_usage, sameFrameCount, frameCount * sizeof(ulong), (byte*)pFrames);
        }

        // Cleanup threads that are no longer active
        foreach (var previousActiveThreadId in _activeThreadIds)
        {
            if (!_currentThreadIds.Contains(previousActiveThreadId))
            {
                if (_threadIdToCompressedFrameIndex.Remove(previousActiveThreadId, out var compressedFrameIndex))
                {
                    _freeCompressedFramesIndices.Add(compressedFrameIndex);
                }
            }
        }

        // Swap the active and current thread ids
        (_currentThreadIds, _activeThreadIds) = (_activeThreadIds, _currentThreadIds);
    }

    private int ComputeSameFrameCount(ulong threadId, int frameCount, ulong* frames)
    {
        int originalFrameCount = frameCount;
        // We limit the frame recording to MaximumCompressedFrameCount
        frameCount = Math.Min(frameCount, MaximumCompressedFrameCount);

        bool hasCompressedFrames = true;
        if (!_threadIdToCompressedFrameIndex.TryGetValue(threadId, out var index))
        {
            if (_freeCompressedFramesIndices.Count > 0)
            {
                index = _freeCompressedFramesIndices.RemoveLast();
                hasCompressedFrames = false;
            }
            else
            {
                // We are full, no compressed frame
                return 0;
            }

            _threadIdToCompressedFrameIndex.Add(threadId, index);
        }

        int sameFrameCount = 0;

        ref var allCompressedFrames = ref MemoryMarshal.GetArrayDataReference(_allCompressedFrames);
        var indexInCompressedFrames = index * MaximumCompressedFrameTotalCount;
        ref ulong previousFrame = ref Unsafe.Add(ref allCompressedFrames, indexInCompressedFrames);

        if (hasCompressedFrames)
        {
            int previousFrameCount = (int)previousFrame;
            previousFrame = ref Unsafe.Add(ref previousFrame, 1);

            var maxFrameCount = Math.Min(previousFrameCount, frameCount);
            for (; sameFrameCount < maxFrameCount; sameFrameCount++)
            {
                if (frames[originalFrameCount - sameFrameCount - 1] != previousFrame)
                {
                    break;
                }

                previousFrame = ref Unsafe.Add(ref previousFrame, 1);
            }
        }

        // Copy the new frames to the current frames
        previousFrame = ref Unsafe.Add(ref allCompressedFrames, indexInCompressedFrames);
        previousFrame = (ulong)frameCount;
        previousFrame = ref Unsafe.Add(ref previousFrame, 1);
        for (int i = 0; i < frameCount; i++)
        {
            Unsafe.Add(ref previousFrame, i) = frames[originalFrameCount - i - 1];
        }

        return sameFrameCount;
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