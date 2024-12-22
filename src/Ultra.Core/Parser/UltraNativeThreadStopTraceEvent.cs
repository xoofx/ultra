// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing;

namespace Ultra.Core;

internal sealed class UltraNativeThreadStopTraceEvent : TraceEvent
{
    private static readonly string[] _payloadNames =
    [
        nameof(SamplingId),
        nameof(FrameThreadId),
    ];

    private Action<UltraNativeThreadStopTraceEvent>? _target;

    internal UltraNativeThreadStopTraceEvent(Action<UltraNativeThreadStopTraceEvent>? target, int eventID, int task, string taskName, Guid taskGuid, int opcode, string opcodeName, Guid providerGuid, string providerName) : base(eventID, task, taskName, taskGuid, opcode, opcodeName, providerGuid, providerName)
    {
        _target = target;
    }

    public ulong SamplingId => (ulong)GetInt64At(0);

    public ulong FrameThreadId => (ulong)GetInt64At(8);

    /// <inheritdoc />

    public override object PayloadValue(int index)
    {
        switch (index)
        {
            case 0:
                return SamplingId;
            case 1:
                return FrameThreadId;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public override string[] PayloadNames => _payloadNames;

    /// <inheritdoc />
    protected override Delegate? Target
    {
        get => _target;
        set => _target = (Action<UltraNativeThreadStopTraceEvent>?)value;
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