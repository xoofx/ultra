// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Numerics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Ultra.Core.Model;
using XenoAtom.Collections;

namespace Ultra.Core;

/// <summary>
/// Internal class to process events from an EventPipe session (Sampler and CLR)
/// </summary>
internal class UltraEventPipeProcessor
{
    private readonly EventPipeEventSource _samplerEventSource;
    private readonly UltraSamplerParser _samplerParser;

    private readonly UTraceProcess _process = new();
    private readonly UTraceModuleList _modules;
    private readonly UTraceManagedMethodList _managedMethods;


    private readonly EventPipeEventSource? _clrEventSource;
    private readonly ClrRundownTraceEventParser? _clrRundownTraceEventParser;

    public UltraEventPipeProcessor(EventPipeEventSource samplerEventSource)
    {
        _samplerEventSource = samplerEventSource;
        _modules = _process.Modules;
        _managedMethods = _process.ManagedMethods;

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
        _clrEventSource.Clr.MethodUnloadVerbose += ProcessMethodLoadVerbose;
        _clrEventSource.Clr.MethodDCStopVerboseV2 += ProcessMethodLoadVerbose;
        _clrRundownTraceEventParser.MethodDCStopVerbose += ProcessMethodLoadVerbose;

        // MethodILToNativeMapTraceData
        _clrEventSource.Clr.MethodILToNativeMap += ProcessMethodILToNativeMap;
        _clrRundownTraceEventParser.MethodILToNativeMapDCStop += ProcessMethodILToNativeMap;
    }

    private void ProcessMethodILToNativeMap(MethodILToNativeMapTraceData obj)
    {
        if (!_managedMethods.TryFindMethodById(obj.MethodID, out var method))
        {
            return;

        }

        int validOffsets = 0;
        for (var i = 0; i < obj.CountOfMapEntries; i++)
        {
            if (obj.ILOffset(i) >= 0) validOffsets++;
        }

        if (validOffsets <= 0)
        {
            return;
        }

        var ilToNativeOffsets = new UNativeILOffset[validOffsets];
        int validOffsetIndex = 0;
        for (var i = 0; i < obj.CountOfMapEntries; i++)
        {
            if (obj.ILOffset(i) >= 0)
            {
                ilToNativeOffsets[validOffsetIndex] = new UNativeILOffset(obj.ILOffset(i), obj.NativeOffset(i));
                validOffsetIndex++;
            }
        }

        method.ILToNativeILOffsets = ilToNativeOffsets;

        // Sort if we have more than one valid offsets
        if (validOffsets > 1)
        {
            // Sort the native offsets in ascending order (for binary search)
            // (Unclear why it would come not sorted, but it seems that TraceLog is still sorting them just in case)
            var span = ilToNativeOffsets.AsSpan();
            span.SortByRef(new UNativeOffsetComparer());
        }
    }

    private void ProcessMethodUnloadVerbose(MethodLoadUnloadVerboseTraceData obj)
    {
    }

    private void ProcessMethodLoadVerbose(MethodLoadUnloadVerboseTraceData method)
    {
        _managedMethods.GetOrCreateManagedMethod(method.ThreadID, method.ModuleID, method.MethodID, method.MethodNamespace, method.MethodName, method.MethodSignature, method.MethodToken, method.MethodFlags, method.MethodStartAddress, (ulong)method.MethodSize);
    }

    private void ProcessModuleLoadUnload(ModuleLoadUnloadTraceData data, bool isLoad, bool isDCStartStop)
    {
    }
    
    private void SamplerParserOnEventNativeModule(UltraNativeModuleTraceEvent evt)
    {
        if (evt.ModulePath is not null)
        {
            if (evt.NativeModuleEventKind == UltraSamplerNativeModuleEventKind.Unloaded)
            {
                // TODO: support undo
                //_mapModuleNameToIndex.Remove(evt.ModulePath);
            }
            else
            {
                _modules.GetOrCreateLoadedModule(evt.ModulePath, evt.LoadAddress, evt.Size);
            }
        }

    }

    private void SamplerParserOnEventNativeCallstack(UltraNativeCallstackTraceEvent obj)
    {
        PrintCallStack(obj);
    }
    
    private void PrintCallStack(UltraNativeCallstackTraceEvent callstackTraceEvent)
    {
        Console.WriteLine($"Thread: {callstackTraceEvent.FrameThreadId}, State: {callstackTraceEvent.ThreadState}, Cpu: {callstackTraceEvent.ThreadCpuUsage}, SameFrameCount: {callstackTraceEvent.PreviousFrameCount}, FrameCount: {callstackTraceEvent.FrameSize / sizeof(ulong)} ");

        var span = callstackTraceEvent.FrameAddresses;
        for (var i = 0; i < span.Length; i++)
        {
            var frame = (UAddress)span[i];
            if (_modules.TryFindModuleByAddress(frame, out var module))
            {
                Console.WriteLine($"  {module.ModuleFile.FilePath}+{frame - module.BaseAddress} (Module: {module.BaseAddress} Address: {frame})");
            }
            else
            {
                if (_managedMethods.TryFindMethodByAddress(frame, out var method))
                {
                    Console.WriteLine($"  {method.MethodNamespace}.{method.MethodName}+{frame - method.MethodStartAddress} (Method: {method.MethodStartAddress} Address: {frame})");
                }
                else
                {
                    Console.WriteLine($"  {frame}");
                }
            }
        }
    }

    public void Run()
    {
        // Run CLR if available
        _clrEventSource?.Process();

        _managedMethods.SortMethodAddressRanges();

        // Run sampler before CLR
        _samplerEventSource.Process();
    }

}
