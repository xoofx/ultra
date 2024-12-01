using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ultra.Sampler;

using static libSystem;

public static class UltraSampler
{
    private static readonly object Lock = new();
    private static Thread? _thread;
    private static bool _stopped;
    private const int MaximumFrames = 65536;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)], EntryPoint = "ultra_sampler_start")]
    internal static void NativeStart() => Start();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)], EntryPoint = "ultra_sampler_stop")]
    internal static void NativeStop() => Stop();

    public static void Start()
    {
        lock (Lock)
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
    }

    public static void Stop()
    {
        lock (Lock)
        {
            if (_thread is null) return;

            _stopped = true;
            _thread.Join();
            _thread = null;
            _stopped = false;
        }
    }

    private static void RunImpl()
    {
        try
        {
            task_for_pid(mach_task_self(), Process.GetCurrentProcess().Id, out var rootTask)
                .ThrowIfError("task_for_pid");

            pthread_threadid_np(0, out var currentThreadId)
                .ThrowIfError("pthread_threadid_np");

            var frames = GC.AllocateUninitializedArray<ulong>(MaximumFrames, pinned: true);

            while (!_stopped)
            {
                if (UltraSamplerSource.Log.IsEnabled())
                {
                    Sample(rootTask, currentThreadId, frames);
                }

                // Sleep for 1ms
                Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Ultra-Sampler unexpected exception while sampling: {ex}");
        }
    }

    private static unsafe void Sample(mach_port_t rootTask, ulong currentThreadId, Span<ulong> frames)
    {
        mach_port_t* taskList;
        task_threads(rootTask, &taskList, out uint taskCount)
            .ThrowIfError("task_threads");

        thread_identifier_info threadInfo = new();

        ulong* pFrames = (ulong*)Unsafe.AsPointer(ref frames[0]);

        for (var i = 0; i < taskCount; i++)
        {
            var threadPort = taskList[i];
            try
            {
                int threadInfoCount = THREAD_IDENTIFIER_INFO_COUNT;
                thread_info(threadPort, THREAD_IDENTIFIER_INFO, out threadInfo, ref threadInfoCount)
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

                thread_suspend(taskList[i])
                    .ThrowIfError("thread_suspend");

                try
                {
                    arm_thread_state64_t armThreadState = new arm_thread_state64_t();
                    int armThreadStateCount = ARM_THREAD_STATE64_COUNT;

                    thread_get_state(threadPort, ARM_THREAD_STATE64, (nint)(void*)&armThreadState, ref armThreadStateCount)
                        .ThrowIfError("thread_get_state");

                    //Console.WriteLine($"sp: 0x{armThreadState.__sp:X8}, fp: 0x{armThreadState.__fp:X8}, lr: 0x{armThreadState.__lr:X8}");
                    WalkCallStack(armThreadState.__sp, armThreadState.__fp, armThreadState.__lr, pFrames);
                }
                finally
                {
                    thread_resume(threadPort)
                        .ThrowIfError("thread_resume");
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Ultra-Sampler unexpected exception while sampling thread #{threadInfo.thread_id}: {ex}");
            }
        }
    }

    private static unsafe void WalkCallStack(ulong sp, ulong fp, ulong lr, ulong* frames)
    {
        int frameIndex = 0;
        while (fp != 0 && frameIndex < MaximumFrames)
        {
            frames[frameIndex++] = lr;
            //sp = fp + 16;
            lr = *(ulong*)(fp + 8);
            fp = *(ulong*)fp;
        }

        UltraSamplerSource.Log.Callstack(frames, frameIndex);
    }
}
