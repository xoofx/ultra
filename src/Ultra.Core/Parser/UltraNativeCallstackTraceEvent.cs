// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing;
using Ultra.Sampler;

namespace Ultra.Core;

internal sealed class UltraNativeCallstackTraceEvent : TraceEvent
{
    private static readonly string[] _payloadNames =
    [
        nameof(FrameThreadId),
        nameof(ThreadState),
        nameof(ThreadCpuUsage),
        nameof(PreviousFrameCount),
        nameof(FrameSize),
        nameof(FrameAddresses)
    ];

    private Action<UltraNativeCallstackTraceEvent>? _target;

    internal UltraNativeCallstackTraceEvent(Action<UltraNativeCallstackTraceEvent>? target, int eventID, int task, string taskName, Guid taskGuid, int opcode, string opcodeName, Guid providerGuid, string providerName) : base(eventID, task, taskName, taskGuid, opcode, opcodeName, providerGuid, providerName)
    {
        _target = target;
    }

    public ulong FrameThreadId => (ulong)GetInt64At(0);

    public UltraSamplerThreadState ThreadState => (UltraSamplerThreadState)GetInt32At(8);

    public double ThreadCpuUsage => GetInt32At(12) / 1000.0;

    public int PreviousFrameCount => GetInt32At(16);
    
    public int FrameSize => GetInt32At(20);

    public unsafe ReadOnlySpan<ulong> FrameAddresses => new((byte*)DataStart + 24, FrameSize / sizeof(ulong));

    /// <inheritdoc />

    public override object PayloadValue(int index)
    {
        switch (index)
        {
            case 0:
                return FrameThreadId;
            case 1:
                return (int)ThreadState;
            case 2:
                return GetInt32At(12);
            case 3:
                return PreviousFrameCount;
            case 4:
                return FrameSize;
            case 5:
                return FrameAddresses.ToArray();
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public override string[] PayloadNames => _payloadNames;

    /// <inheritdoc />
    protected override Delegate? Target
    {
        get => _target;
        set => _target = (Action<UltraNativeCallstackTraceEvent>?)value;
    }

    /// <inheritdoc />
    protected override void Dispatch()
    {
        _target?.Invoke(this);
    }

    /// <inheritdoc />
    protected override void Validate()
    {
    }
}