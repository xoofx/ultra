// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using Ultra.Sampler.MacOS;

namespace Ultra.Sampler;

[EventSource(Name = UltraSamplerParser.Name, Guid = UltraSamplerParser.IdAsString)]
internal sealed class UltraSamplerSource : EventSource
{
    public static readonly UltraSamplerSource Log = new();

    private UltraSamplerSource()
    {
    }

    [Event(UltraSamplerParser.NativeCallStackEvent, Level = EventLevel.Informational)]
    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    public unsafe void OnNativeCallstack(ulong threadId, nint pFrames, int count)
    {
        EventData2 evt = default;
        evt.Data1.DataPointer = (nint)(void*)&threadId;
        evt.Data1.Size = sizeof(ulong);
        evt.Data2.DataPointer = (nint)pFrames;
        evt.Data2.Size = count * sizeof(ulong);
        WriteEventCore(UltraSamplerParser.NativeCallStackEvent, 2, &evt.Data1);
    }

    [Event(UltraSamplerParser.NativeModuleEvent, Level = EventLevel.Informational)]
    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    public unsafe void OnNativeModuleEvent(NativeModuleEventKind nativeModuleEventKind, ulong loadAddress, ulong size, byte[]? modulePathUtf8, long timestampUtc)
    {
        EventData5 evt = default;
        evt.Data1.DataPointer = (nint)(void*)&nativeModuleEventKind;
        evt.Data1.Size = sizeof(int);
        evt.Data2.DataPointer = (nint)(void*)&loadAddress;
        evt.Data2.Size = sizeof(ulong);
        evt.Data3.DataPointer = (nint)(void*)&size;
        evt.Data3.Size = sizeof(ulong);
        fixed (byte* evtPathPtr = modulePathUtf8)
        {
            evt.Data4.DataPointer = (nint)evtPathPtr;
            evt.Data4.Size = modulePathUtf8?.Length ?? 0;
            evt.Data5.DataPointer = (nint)(void*)&timestampUtc;
            evt.Data5.Size = sizeof(long);
            WriteEventCore(UltraSamplerParser.NativeModuleEvent, 4, &evt.Data1);
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

    private struct EventData2
    {
        public EventData Data1;

        public EventData Data2;
    }

    private struct EventData5
    {
        public EventData Data1;

        public EventData Data2;

        public EventData Data3;

        public EventData Data4;

        public EventData Data5;
    }
}