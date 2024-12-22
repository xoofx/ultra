// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text;
using Microsoft.Diagnostics.Tracing;
using Ultra.Sampler;

namespace Ultra.Core;

internal sealed class UltraNativeThreadStartTraceEvent : TraceEvent
{
    private static readonly string[] _payloadNames =
    [
        nameof(SamplingId),
        nameof(FrameThreadId),
        nameof(ThreadNameSize),
        nameof(ThreadName),
    ];

    private Action<UltraNativeThreadStartTraceEvent>? _target;

    internal UltraNativeThreadStartTraceEvent(Action<UltraNativeThreadStartTraceEvent>? target, int eventID, int task, string taskName, Guid taskGuid, int opcode, string opcodeName, Guid providerGuid, string providerName) : base(eventID, task, taskName, taskGuid, opcode, opcodeName, providerGuid, providerName)
    {
        _target = target;
    }

    public ulong SamplingId => (ulong)GetInt64At(0);

    public ulong FrameThreadId => (ulong)GetInt64At(8);

    public int ThreadNameSize => GetInt32At(16);

    public unsafe byte* ThreadNamePointer => (byte*)DataStart + 24 + ThreadNameSize;
    
    public unsafe string ThreadName => Encoding.UTF8.GetString(new ReadOnlySpan<byte>(ThreadNamePointer, ThreadNameSize));

    /// <inheritdoc />

    public override object PayloadValue(int index)
    {
        switch (index)
        {
            case 0:
                return SamplingId;
            case 1:
                return FrameThreadId;
            case 2:
                return ThreadNameSize;
            case 3:
                unsafe
                {
                    return new ReadOnlySpan<byte>(ThreadNamePointer, ThreadNameSize).ToArray();
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public override string[] PayloadNames => _payloadNames;

    /// <inheritdoc />
    protected override Delegate? Target
    {
        get => _target;
        set => _target = (Action<UltraNativeThreadStartTraceEvent>?)value;
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