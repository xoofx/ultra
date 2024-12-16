// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text;
using Microsoft.Diagnostics.Tracing;
using Ultra.Sampler;

namespace Ultra.Core;

public sealed class UltraSamplerParser : TraceEventParser
{
    public static readonly Guid ProviderGuid = UltraSamplerConstants.ProviderGuid;

    public const string ProviderName = UltraSamplerConstants.ProviderName;

    private static volatile TraceEvent[]? _templates;
    
    public UltraSamplerParser(TraceEventSource source) : base(source)
    {
    }

    public event Action<UltraNativeCallstackTraceEvent> EventNativeCallstack
    {
        add => source.RegisterEventTemplate(CreateUltraNativeCallstackTraceEvent(value));
        remove => source.UnregisterEventTemplate(value, UltraSamplerConstants.NativeCallStackEventId, ProviderGuid);
    }

    public event Action<UltraNativeModuleTraceEvent> EventNativeModule
    {
        add => source.RegisterEventTemplate(CreateUltraNativeModuleTraceEvent(value));
        remove => source.UnregisterEventTemplate(value, UltraSamplerConstants.NativeModuleEventId, ProviderGuid);
    }
    
    /// <inheritdoc />
    protected override string GetProviderName() => UltraSamplerConstants.ProviderName;

    /// <inheritdoc />
    protected override void EnumerateTemplates(Func<string, string, EventFilterResponse> eventsToObserve, Action<TraceEvent> callback)
    {
        if (_templates == null)
        {
            _templates =
            [
                CreateUltraNativeCallstackTraceEvent(null),
                CreateUltraNativeModuleTraceEvent(null),
            ];
        }

        foreach (var template in _templates)
        {
            if (eventsToObserve == null || eventsToObserve(template.ProviderName, template.EventName) == EventFilterResponse.AcceptEvent)
            {
                callback(template);
            }
        }
    }

    private static TraceEvent CreateUltraNativeCallstackTraceEvent(Action<UltraNativeCallstackTraceEvent>? value)
        => new UltraNativeCallstackTraceEvent(value, UltraSamplerConstants.NativeCallStackEventId, 0, "OnNativeCallStack", Guid.Empty, 0, "OnNativeCallStack", ProviderGuid, ProviderName);

    private static TraceEvent CreateUltraNativeModuleTraceEvent(Action<UltraNativeModuleTraceEvent>? value)
        => new UltraNativeModuleTraceEvent(value, UltraSamplerConstants.NativeModuleEventId, 0, "OnNativeModule", Guid.Empty, 0, "OnNativeModule", ProviderGuid, ProviderName);
}

public sealed class UltraNativeCallstackTraceEvent : TraceEvent
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

public sealed class UltraNativeModuleTraceEvent : TraceEvent
{
    private static readonly string[] _payloadNames =
    [
        nameof(NativeModuleEventKind),
        nameof(LoadAddress),
        nameof(Size),
        nameof(TimestampUtc),
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

    private int ModulePathUtf8Length => GetInt32At(28);

    private unsafe ReadOnlySpan<byte> ModulePathUtf8 => ModulePathUtf8Length == 0 ? ReadOnlySpan<byte>.Empty : new((byte*)DataStart + 32, ModulePathUtf8Length);

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
                return ModulePathUtf8Length;
            case 5:
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