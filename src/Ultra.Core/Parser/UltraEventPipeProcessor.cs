// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics.Tracing;
using System.Numerics;
using System.Runtime.CompilerServices;
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
    private UnsafeDictionary<ulong, ThreadSamplerState> _threadSamplingStates = new();
    
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
        _samplerParser.EventNativeThreadStart += SamplerParserOnEventNativeThreadStart;
        _samplerParser.EventNativeThreadStop += SamplerParserOnEventNativeThreadStop;
        _samplerParser.Source.Dynamic.AddCallbackForProviderEvent("Microsoft-DotNETCore-EventPipe", "ProcessInfo", SamplerProcessInfo);
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
            span.SortByRef(new UNativeILOffsetComparer());
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
        var module = _modules.GetOrCreateManagedModule(data.ModuleID, data.AssemblyID, data.ModuleILPath);

        module.ModuleFile.SymbolUuid = data.ManagedPdbSignature;
        module.ModuleFile.SymbolFilePath = data.ManagedPdbBuildPath;

        if (!isDCStartStop)
        {
            if (isLoad)
            {
                module.LoadTime = UTimeSpan.FromMilliseconds(data.TimeStampRelativeMSec);
            }
            else
            {
                module.UnloadTime = UTimeSpan.FromMilliseconds(data.TimeStampRelativeMSec);
            }
        }
    }
    
    private void SamplerParserOnEventNativeModule(UltraNativeModuleTraceEvent evt)
    {
        if (evt.ModulePath is not null)
        {
            var module = _modules.GetOrCreateNativeModule(evt.LoadAddress, evt.Size, evt.ModulePath);

            if (evt.NativeModuleEventKind == UltraSamplerNativeModuleEventKind.Unloaded)
            {
                // TODO: how to support remove?
                //_mapModuleNameToIndex.Remove(evt.ModulePath);
                module.UnloadTime = UTimeSpan.FromMilliseconds(evt.TimeStampRelativeMSec);
            }
            else
            {
                module.ModuleFile.SymbolUuid = evt.Uuid;
                module.LoadTime = UTimeSpan.FromMilliseconds(evt.TimeStampRelativeMSec);
            }
        }

    }

    private void SamplerParserOnEventNativeCallstack(UltraNativeCallstackTraceEvent callstackTraceEvent)
    {
        GetThreadSamplingState(callstackTraceEvent.FrameThreadId).RecordStack(_process, callstackTraceEvent);
    }

    private void SamplerParserOnEventNativeThreadStart(UltraNativeThreadStartTraceEvent obj)
    {
        var thread = GetThreadSamplingState(obj.FrameThreadId).Thread;
        thread.StartTime = UTimeSpan.FromMilliseconds(obj.TimeStampRelativeMSec);
        thread.Name = obj.ThreadName;
    }

    private void SamplerParserOnEventNativeThreadStop(UltraNativeThreadStopTraceEvent obj)
    {
        var thread = GetThreadSamplingState(obj.FrameThreadId).Thread;
        thread.StopTime = UTimeSpan.FromMilliseconds(obj.TimeStampRelativeMSec);
    }

    public UTraceSession Run()
    {
        // Run CLR if available
        _clrEventSource?.Process();

        _managedMethods.SortMethodAddressRanges();

        // Run sampler before CLR
        _samplerEventSource.Process();

        var session = new UTraceSession();
        session.Processes.Add(_process);

        session.NumberOfProcessors = _samplerEventSource.NumberOfProcessors;
        session.StartTime = _samplerEventSource.SessionStartTime;
        session.Duration = _samplerEventSource.SessionDuration;
        session.CpuSpeedMHz = _samplerEventSource.CpuSpeedMHz;

        return session;
    }

    private ThreadSamplerState GetThreadSamplingState(ulong threadID)
    {
        if (!_threadSamplingStates.TryGetValue(threadID, out var threadSamplingState))
        {
            var thread = _process.Threads.GetOrCreateThread(threadID);
            threadSamplingState = new(thread);
            _threadSamplingStates.Add(threadID, threadSamplingState);
        }
        return threadSamplingState;
    }

    private void SamplerProcessInfo(TraceEvent obj)
    {
        var osInformation = obj.PayloadByName("OSInformation") as string;
        var archInformation = obj.PayloadByName("ArchInformation") as string;
        //Console.WriteLine(osInformation);
    }


    private class ThreadSamplerState
    {
        private readonly UCodeAddressIndex[] _previousFrame = new UCodeAddressIndex[63]; // 64 - 1 as in the sampler, the first index is used for the count
        private UnsafeList<UCodeAddressIndex> _callStack = new(1024);
        private readonly UTraceThread _thread;

        public ThreadSamplerState(UTraceThread thread)
        {
            _thread = thread;
        }

        public UTraceThread Thread => _thread;

        public void RecordStack(UTraceProcess process, UltraNativeCallstackTraceEvent evt)
        {
            _callStack.Clear();
            var newFrameAddresses = evt.FrameAddresses;
            var callStackCount = evt.PreviousFrameCount + newFrameAddresses.Length;
            if (callStackCount == 0) return;
            _callStack.UnsafeSetCount(callStackCount);

            ref var callStackIt = ref _callStack.UnsafeGetRefAt(0);

            var codeAddresses = process.CodeAddresses;
            foreach (var address in newFrameAddresses)
            {
                var frame = (UAddress)address;
                var codeAddressIndex = codeAddresses.GetOrCreateAddress(frame);
                callStackIt = codeAddressIndex;
                callStackIt = ref Unsafe.Add(ref callStackIt, 1);
            }

            var previousFrameCount = evt.PreviousFrameCount;
            if (previousFrameCount > 0)
            {
                var previousFrame = _previousFrame;
                for (var i = 0; i < previousFrameCount; i++)
                {
                    callStackIt = previousFrame[i];
                    callStackIt = ref Unsafe.Add(ref callStackIt, 1);
                }
            }
            
            // Copy the new frame to the previous frame
            var callStack = _callStack.AsSpan();
            var maxLengthToCopy = Math.Min(_previousFrame.Length, callStack.Length);
            for (var i = 0; i < maxLengthToCopy; i++)
            {
                _previousFrame[i] = callStack[callStack.Length - maxLengthToCopy + i];
            }

            var callStackIndex = process.CallStacks.InsertCallStack(callStack);

            _thread.AddSample(new(callStackIndex, UTimeSpan.FromMilliseconds(evt.TimeStampRelativeMSec), TimeSpan.Zero)); // TODO: cputime
        }
    }
}
