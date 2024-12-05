// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

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

    [Event(UltraSamplerParser.NativeCallStackEvent, Level = EventLevel.Verbose, Message = "NativeCallstackEvent Thread {0} with {2} frames")]
    [SkipLocalsInit]
    public unsafe void OnNativeCallstack(ulong threadId, ulong* pFrames, int count)
    {

        Unsafe.SkipInit(out EventData2 evt);
        evt.Data1.DataPointer = (nint)(void*)&threadId;
        evt.Data1.Size = sizeof(ulong);
        evt.Data2.DataPointer = (nint)pFrames;
        evt.Data2.Size = count * sizeof(ulong);
        WriteEventCore(UltraSamplerParser.NativeCallStackEvent, 2, &evt.Data1);
    }

    [Event(UltraSamplerParser.NativeModuleEvent, Level = EventLevel.Verbose, Message = "NativeModuleEvent {0} LoadAddress: {1}")]
    [SkipLocalsInit]
    public unsafe void OnNativeModuleEvent(NativeModuleEventKind nativeModuleEventKind, ulong loadAddress, byte[]? modulePathUtf8, long timestampUtc)
    {
        Unsafe.SkipInit(out EventData4 evt);
        evt.Data1.DataPointer = (nint)(void*)&nativeModuleEventKind;
        evt.Data1.Size = sizeof(int);
        evt.Data2.DataPointer = (nint)(void*)&loadAddress;
        evt.Data2.Size = sizeof(ulong);
        fixed (byte* evtPathPtr = modulePathUtf8)
        {
            evt.Data3.DataPointer = (nint)evtPathPtr;
            evt.Data3.Size = modulePathUtf8?.Length ?? 0;
            evt.Data4.DataPointer = (nint)(void*)&timestampUtc;
            evt.Data4.Size = sizeof(long);
            WriteEventCore(UltraSamplerParser.NativeModuleEvent, 4, &evt.Data1);
        }
    }

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            UltraSampler.Instance.Enable();
        }
        else if (command.Command == EventCommand.Disable)
        {
            UltraSampler.Instance.Disable();
        }
    }

    private struct EventData2
    {
        public EventData Data1;

        public EventData Data2;
    }

    private struct EventData4
    {
        public EventData Data1;

        public EventData Data2;

        public EventData Data3;

        public EventData Data4;
    }
}