// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.Tracing;
using Ultra.Sampler;

namespace Ultra.Core;

internal sealed class UltraSamplerParser : TraceEventParser
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

    public event Action<UltraNativeThreadStartTraceEvent> EventNativeThreadStart
    {
        add => source.RegisterEventTemplate(CreateUltraNativeThreadStartTraceEvent(value));
        remove => source.UnregisterEventTemplate(value, UltraSamplerConstants.NativeThreadStartEventId, ProviderGuid);
    }

    public event Action<UltraNativeThreadStopTraceEvent> EventNativeThreadStop
    {
        add => source.RegisterEventTemplate(CreateUltraNativeThreadStopTraceEvent(value));
        remove => source.UnregisterEventTemplate(value, UltraSamplerConstants.NativeThreadStopEventId, ProviderGuid);
    }

    public event Action<UltraNativeProcessStartTraceEvent> EventNativeProcessStart
    {
        add => source.RegisterEventTemplate(CreateUltraNativeProcessStartTraceEvent(value));
        remove => source.UnregisterEventTemplate(value, UltraSamplerConstants.NativeProcessStartEventId, ProviderGuid);
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
                CreateUltraNativeThreadStartTraceEvent(null),
                CreateUltraNativeThreadStopTraceEvent(null),
                CreateUltraNativeProcessStartTraceEvent(null)
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
        => new UltraNativeCallstackTraceEvent(value, UltraSamplerConstants.NativeCallStackEventId, 0, "NativeCallStack", Guid.Empty, 0, "NativeCallStack", ProviderGuid, ProviderName);

    private static TraceEvent CreateUltraNativeModuleTraceEvent(Action<UltraNativeModuleTraceEvent>? value)
        => new UltraNativeModuleTraceEvent(value, UltraSamplerConstants.NativeModuleEventId, 0, "NativeModule", Guid.Empty, 0, "NativeModule", ProviderGuid, ProviderName);

    private static TraceEvent CreateUltraNativeThreadStartTraceEvent(Action<UltraNativeThreadStartTraceEvent>? value)
        => new UltraNativeThreadStartTraceEvent(value, UltraSamplerConstants.NativeThreadStartEventId, 0, "NativeThreadStart", Guid.Empty, 0, "NativeThreadStart", ProviderGuid, ProviderName);

    private static TraceEvent CreateUltraNativeThreadStopTraceEvent(Action<UltraNativeThreadStopTraceEvent>? value)
        => new UltraNativeThreadStopTraceEvent(value, UltraSamplerConstants.NativeThreadStopEventId, 0, "NativeThreadStop", Guid.Empty, 0, "NativeThreadStop", ProviderGuid, ProviderName);

    private static TraceEvent CreateUltraNativeProcessStartTraceEvent(Action<UltraNativeProcessStartTraceEvent>? value)
        => new UltraNativeProcessStartTraceEvent(value, UltraSamplerConstants.NativeProcessStartEventId, 0, "NativeProcessStart", Guid.Empty, 0, "NativeProcessStart", ProviderGuid, ProviderName);
}