// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Ultra.Core;

/// <summary>
/// Internal class to process events from an EventPipe session (Sampler and CLR)
/// </summary>
internal class UltraEventPipeProcessor
{
    private readonly EventPipeEventSource _samplerEventSource;
    private readonly UltraSamplerParser _samplerParser;

    private readonly EventPipeEventSource? _clrEventSource;
    private readonly ClrRundownTraceEventParser? _clrRundownTraceEventParser;
    
    public UltraEventPipeProcessor(EventPipeEventSource samplerEventSource)
    {
        _samplerEventSource = samplerEventSource;

        _samplerParser = new UltraSamplerParser(samplerEventSource);

        // NativeCallstack and NativeModule
        _samplerParser.EventNativeCallstack += SamplerParserOnEventNativeCallstack;
        _samplerParser.EventNativeModule += SamplerParserOnEventNativeModule;
    }

    public UltraEventPipeProcessor(EventPipeEventSource samplerEventSource, EventPipeEventSource clrEventSource) : this(samplerEventSource)
    {
        _clrEventSource = clrEventSource;
        _clrRundownTraceEventParser = new ClrRundownTraceEventParser(clrEventSource);

        // ManagedModuleLoadOrUnload
        _clrEventSource.Clr.LoaderModuleLoad += delegate (ModuleLoadUnloadTraceData data)
        {
            ProcessModuleLoadUnload(data, true, false);
        };
        _clrEventSource.Clr.LoaderModuleUnload += delegate (ModuleLoadUnloadTraceData data)
        {
            ProcessModuleLoadUnload(data, false, false);
        };
        _clrEventSource.Clr.LoaderModuleDCStopV2 += delegate (ModuleLoadUnloadTraceData data)
        {
            ProcessModuleLoadUnload(data, false, true);
        };

        _clrRundownTraceEventParser.LoaderModuleDCStop += data =>
        {
            ProcessModuleLoadUnload(data, false, true);
        };
        _clrRundownTraceEventParser.LoaderModuleDCStart += data =>
        {
            ProcessModuleLoadUnload(data, false, true);
        };

        // MethodLoad
        _clrEventSource.Clr.MethodLoadVerbose += ProcessMethodLoadVerbose;
        _clrEventSource.Clr.MethodDCStartVerboseV2 += ProcessMethodLoadVerbose;
        _clrRundownTraceEventParser.MethodDCStartVerbose += ProcessMethodLoadVerbose;

        // MethodUnload
        _clrEventSource.Clr.MethodUnloadVerbose += ProcessMethodUnloadVerbose;
        _clrEventSource.Clr.MethodDCStopVerboseV2 += ProcessMethodUnloadVerbose;
        _clrRundownTraceEventParser.MethodDCStopVerbose += ProcessMethodUnloadVerbose;

        // MethodILToNativeMapTraceData
        _clrEventSource.Clr.MethodILToNativeMap += ProcessMethodILToNativeMap;
        _clrRundownTraceEventParser.MethodILToNativeMapDCStop += ProcessMethodILToNativeMap;
    }

    private void ProcessMethodILToNativeMap(MethodILToNativeMapTraceData obj)
    {
    }

    private void ProcessMethodUnloadVerbose(MethodLoadUnloadVerboseTraceData obj)
    {
    }

    private void ProcessMethodLoadVerbose(MethodLoadUnloadVerboseTraceData obj)
    {
    }

    private void ProcessModuleLoadUnload(ModuleLoadUnloadTraceData data, bool isLoad, bool isDCStartStop)
    {
    }
    
    private void SamplerParserOnEventNativeModule(UltraNativeModuleTraceEvent obj)
    {
    }

    private void SamplerParserOnEventNativeCallstack(UltraNativeCallstackTraceEvent obj)
    {
    }

    public void Run()
    {
        // Run CLR if available
        _clrEventSource?.Process();

        // Run sampler before CLR
        _samplerEventSource.Process();
    }













}