// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;

namespace Ultra.Sampler;

[EventSource(Name = UltraSamplerConstants.ProviderName)] // Cannot set the ProviderGuid that is not used https://github.com/dotnet/diagnostics/issues/389
internal sealed class UltraSamplerSource : EventSource
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(UltraSamplerSource))]
    public static readonly UltraSamplerSource Log = new();

    private UltraSamplerSource()
    {
    }

    [Event(UltraSamplerConstants.NativeCallStackEventId, Level = EventLevel.Informational, Task = (EventTask)UltraSamplerConstants.TaskNativeCallStackEventId)]
    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    public unsafe void NativeCallstack(ulong threadId, int threadState, int threadCpuUsage, int previousFrameCount, int framesSize, byte* frames) // frames is last to allow perfview to visualize previous fixed size arguments and also, it is an ulong otherwise the EventSource will silently fail to register!
    {
        var evt = stackalloc EventData[3];
        evt[0].DataPointer = (nint)(void*)&threadId;
        evt[0].Size = sizeof(ulong);
        evt[1].DataPointer = (nint)(void*)&framesSize;
        evt[1].Size = sizeof(int);
        evt[2].DataPointer = (nint)(void*)&frames;
        evt[2].Size = framesSize;
        WriteEventCore(UltraSamplerConstants.NativeCallStackEventId, 3, evt);
    }

    [Event(UltraSamplerConstants.NativeModuleEventId, Level = EventLevel.Informational, Task = (EventTask)UltraSamplerConstants.TaskNativeModuleEventId)]
    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    public unsafe void NativeModuleEvent(int nativeModuleEventKind, ulong loadAddress, ulong size, DateTime timestampUtc, byte[]? modulePathUtf8) // byte[] is last to allow perfview to visualize previous fixed size arguments
    {
        var evt = stackalloc EventData[6];
        evt[0].DataPointer = (nint)(void*)&nativeModuleEventKind;
        evt[0].Size = sizeof(int);
        evt[1].DataPointer = (nint)(void*)&loadAddress;
        evt[1].Size = sizeof(ulong);
        evt[2].DataPointer = (nint)(void*)&size;
        evt[2].Size = sizeof(ulong);
        var utcFileTime = timestampUtc.ToFileTimeUtc();
        evt[3].DataPointer = (nint)(void*)&utcFileTime;
        evt[3].Size = sizeof(long);
        fixed (byte* evtPathPtr = modulePathUtf8)
        {
            var modulePathUtf8Size = modulePathUtf8?.Length ?? 0;
            evt[4].DataPointer = (nint)(void*)&modulePathUtf8Size;
            evt[4].Size = sizeof(int);

            if (modulePathUtf8Size > 0)
            {
                evt[5].DataPointer = (nint)evtPathPtr;
                evt[5].Size = modulePathUtf8Size;
            }
            else
            {
                evt[5].DataPointer = (nint)(void*)&loadAddress;
                evt[5].Size = 0;
            }

            WriteEventCore(UltraSamplerConstants.NativeModuleEventId, 6, evt);
        }
    }

    [NonEvent]
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            UltraSampler.Instance.Enable();
        }
        else if (command.Command == EventCommand.Disable)
        {
            UltraSampler.Instance.Disable();

            // Wait a bit to let the sampler thread finishing
            Thread.Sleep(100);
        }
    }
}