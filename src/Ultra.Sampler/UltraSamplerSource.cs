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
    
    [Event(UltraSamplerParser.CallStackEvent, Level = EventLevel.Verbose)]
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public unsafe void Callstack(ulong* pFrames, int count)
    {
        if (IsEnabled())
        {
            Unsafe.SkipInit(out EventSource.EventData evt);
            evt.DataPointer = (nint)pFrames;
            evt.Size = count * sizeof(ulong);
            WriteEventCore(UltraSamplerParser.CallStackEvent, 1, &evt);
        }
    }
}