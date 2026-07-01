// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text;
using Microsoft.Diagnostics.Tracing;

namespace Ultra.Core;

internal sealed class UltraNativeModuleTraceEvent : TraceEvent
{
    private static readonly string[] _payloadNames =
    [
        nameof(NativeModuleEventKind),
        nameof(LoadAddress),
        nameof(Size),
        nameof(TimestampUtc),
        nameof(Uuid),
        nameof(ModulePathUtf8Length),
        nameof(ModulePath)
    ];

    private Action<UltraNativeModuleTraceEvent>? _target;

    internal UltraNativeModuleTraceEvent(Action<UltraNativeModuleTraceEvent>? target, int eventID, int task, string taskName, Guid taskGuid, int opcode, string opcodeName, Guid providerGuid, string providerName) : base(eventID, task, taskName, taskGuid, opcode, opcodeName,
        providerGuid,
        providerName)
    {
        _target = target;
    }

    public UltraSamplerNativeModuleEventKind NativeModuleEventKind => (UltraSamplerNativeModuleEventKind)GetInt32At(0);

    public ulong LoadAddress => (ulong)GetInt64At(4);

    public ulong Size => (ulong)GetInt64At(12);

    public DateTime TimestampUtc => DateTime.FromFileTimeUtc(GetInt64At(20));

    public unsafe Guid Uuid => *(Guid*)(DataStart + 28);

    public unsafe int ModulePathUtf8Length => GetInt32At(44);

    public unsafe ReadOnlySpan<byte> ModulePathUtf8 => ModulePathUtf8Length == 0 ? ReadOnlySpan<byte>.Empty : new((byte*)DataStart + 48, ModulePathUtf8Length);

    public unsafe string? ModulePath => ModulePathUtf8Length == 0 ? null : Encoding.UTF8.GetString(ModulePathUtf8);

    /// <inheritdoc />
    public override object? PayloadValue(int index)
    {
        switch (index)
        {
            case 0:
                return (int)NativeModuleEventKind;
            case 1:
                return LoadAddress;
            case 2:
                return Size;
            case 3:
                return TimestampUtc;
            case 4:
                return Uuid;
            case 5:
                return ModulePathUtf8Length;
            case 6:
                return ModulePathUtf8.ToArray();
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    /// <inheritdoc />
    public override string[] PayloadNames => _payloadNames;

    /// <inheritdoc />
    protected override Delegate? Target
    {
        get => _target;
        set => _target = (Action<UltraNativeModuleTraceEvent>?)value;
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