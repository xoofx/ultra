// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;

namespace Ultra.Sampler;

[EventSource(Name = UltraSamplerParser.Name, Guid = UltraSamplerParser.IdAsString)]
public class UltraSamplerSource : EventSource
{
    public static readonly UltraSamplerSource Log = new();

    private UltraSamplerSource()
    {
    }

    [Event(UltraSamplerParser.NativeCallStackEvent, Level = EventLevel.Verbose, Message = "Thread {0} Callstack")]
    [SkipLocalsInit]
    public unsafe void Callstack(ulong threadId, ulong* pFrames, int count)
    {
        if (IsEnabled())
        {
            Unsafe.SkipInit(out EventData2 evt);
            evt.Data1.DataPointer = (nint)(void*)&threadId;
            evt.Data1.Size = sizeof(ulong);
            evt.Data2.DataPointer = (nint)pFrames;
            evt.Data2.Size = count * sizeof(ulong);
            WriteEventCore(UltraSamplerParser.NativeCallStackEvent, 2, &evt.Data1);
        }
    }

    [Event(UltraSamplerParser.NativeModuleEvent, Level = EventLevel.Verbose, Message = "NativeModule {0} Address: {1}")]
    [SkipLocalsInit]
    public unsafe void OnNativeModuleEvent(int evtKind, ulong evtLoadAddress, byte[]? evtPath, DateTime evtTimestampUtc)
    {
        if (IsEnabled())
        {
            Unsafe.SkipInit(out EventData4 evt);
            evt.Data1.DataPointer = (nint)(void*)&evtKind;
            evt.Data1.Size = sizeof(int);
            evt.Data2.DataPointer = (nint)(void*)&evtLoadAddress;
            evt.Data2.Size = sizeof(ulong);
            fixed (byte* evtPathPtr = evtPath)
            {
                evt.Data3.DataPointer = (nint)evtPathPtr;
                evt.Data3.Size = evtPath?.Length ?? 0;
                evt.Data4.DataPointer = (nint)(void*)&evtTimestampUtc;
                evt.Data4.Size = sizeof(DateTime);
                WriteEventCore(UltraSamplerParser.NativeModuleEvent, 4, &evt.Data1);
            }
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