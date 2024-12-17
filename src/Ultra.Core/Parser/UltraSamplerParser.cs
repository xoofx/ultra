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