// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Ultra.Core.Model;
using Ultra.Sampler;
using XenoAtom.Collections;

namespace Ultra.Core;

/// <summary>
/// Internal class to process events from an EventPipe session (Sampler and CLR)
/// </summary>
internal class UltraEventPipeProcessor
{
    private readonly EventPipeEventSource _samplerEventSource;
    private readonly UltraSamplerParser _samplerParser;
    private ProcessState? _currentProcessState;

    private readonly UTraceSession _session = new();

    private readonly UnsafeDictionary<int, ProcessState> _processes = new(1);

    private readonly EventPipeEventSource? _clrEventSource;
    private readonly ClrRundownTraceEventParser? _clrRundownTraceEventParser;

    public UltraEventPipeProcessor(EventPipeEventSource samplerEventSource)
    {
        _samplerEventSource = samplerEventSource;
        _samplerParser = new UltraSamplerParser(samplerEventSource);

        // NativeCallstack and NativeModule
        _samplerParser.EventNativeCallstack += SamplerParserOnEventNativeCallstack;
        _samplerParser.EventNativeModule += SamplerParserOnEventNativeModule;
        _samplerParser.EventNativeThreadStart += SamplerParserOnEventNativeThreadStart;
        _samplerParser.EventNativeThreadStop += SamplerParserOnEventNativeThreadStop;
        _samplerParser.EventNativeProcessStart += SamplerParserOnEventNativeProcessStart;
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
        _clrEventSource.Clr.MethodJittingStarted += ClrOnMethodJittingStarted;
        _clrEventSource.Clr.MethodLoadVerbose += ProcessMethodLoadVerbose;
        _clrEventSource.Clr.MethodDCStartVerboseV2 += ProcessMethodLoadVerbose;
        _clrRundownTraceEventParser.MethodDCStartVerbose += ProcessMethodLoadVerbose;

        // GC Events
        _clrEventSource.Clr.GCHeapStats += ClrOnGCHeapStats;
        _clrEventSource.Clr.GCAllocationTick += ClrOnGCAllocationTick;
        _clrEventSource.Clr.GCStart += ClrOnGCStart;
        _clrEventSource.Clr.GCStop += ClrOnGCStop;
        _clrEventSource.Clr.GCSuspendEEStart += ClrOnGCSuspendEEStart;
        _clrEventSource.Clr.GCSuspendEEStop += ClrOnGCSuspendEEStop;
        _clrEventSource.Clr.GCRestartEEStart += ClrOnGCRestartEEStart;
        _clrEventSource.Clr.GCRestartEEStop += ClrOnGCRestartEEStop;

        // MethodUnload
        _clrEventSource.Clr.MethodUnloadVerbose += ProcessMethodLoadVerbose;
        _clrEventSource.Clr.MethodDCStopVerboseV2 += ProcessMethodLoadVerbose;
        _clrRundownTraceEventParser.MethodDCStopVerbose += ProcessMethodLoadVerbose;

        // MethodILToNativeMapTraceData
        _clrEventSource.Clr.MethodILToNativeMap += ProcessMethodILToNativeMap;
        _clrRundownTraceEventParser.MethodILToNativeMapDCStop += ProcessMethodILToNativeMap;
    }

    private void SamplerParserOnEventNativeProcessStart(UltraNativeProcessStartTraceEvent processStartEvent)
    {
        var processState = GetProcessState(processStartEvent);
        var process = processState.CurrentProcess!;

        process.StartTime = processStartEvent.StartTimeUtc;
        process.ProcessArchitecture = processStartEvent.ProcessArchitecture;
        process.RuntimeIdentifier = processStartEvent.RuntimeIdentifier.ToString();
        process.OSDescription = processStartEvent.OSDescription.ToString();

        processState.SetCurrentProcess(process);
        _session.Processes.Add(process);
    }

    private void ClrOnGCRestartEEStart(GCNoUserDataTraceData gcRestartEE)
    {
        var gcRestartEEMarker = new GCRestartExecutionEngineTraceMarker()
        {
            StartTime = UTimeSpan.FromMilliseconds(gcRestartEE.TimeStampRelativeMSec)
        };
        GetProcessState(gcRestartEE).GetThreadSamplingState((ulong)gcRestartEE.ThreadID).PendingGCRestartExecutionEngineTraceMarkers.Add(gcRestartEEMarker);
    }

    private void ClrOnGCRestartEEStop(GCNoUserDataTraceData gcRestartEE)
    {
        var threadState = GetThreadSamplingState(gcRestartEE, (ulong)gcRestartEE.ThreadID);
        if (threadState.PendingGCRestartExecutionEngineTraceMarkers.Count == 0) return;
        var gcRestartEEMarker = threadState.PendingGCRestartExecutionEngineTraceMarkers.Pop();
        gcRestartEEMarker.Duration = TimeSpan.FromMilliseconds(gcRestartEE.TimeStampRelativeMSec) - gcRestartEEMarker.StartTime.Value;
        threadState.Thread.Markers.Add(gcRestartEEMarker);
    }

    private void ClrOnGCSuspendEEStart(GCSuspendEETraceData gcSuspendEE)
    {
        var gcSuspendEEMarker = new GCSuspendExecutionEngineTraceMarker()
        {
            StartTime = UTimeSpan.FromMilliseconds(gcSuspendEE.TimeStampRelativeMSec),
            Reason = gcSuspendEE.Reason.ToString(),
            Count = gcSuspendEE.Count
        };

        GetThreadSamplingState(gcSuspendEE, (ulong)gcSuspendEE.ThreadID).PendingGCSuspendExecutionEngineTraceMarkers.Add(gcSuspendEEMarker);
    }

    private void ClrOnGCSuspendEEStop(GCNoUserDataTraceData obj)
    {
        var threadState = GetThreadSamplingState(obj, (ulong)obj.ThreadID);
        if (threadState.PendingGCSuspendExecutionEngineTraceMarkers.Count == 0) return;
        var gcSuspendEEMarker = threadState.PendingGCSuspendExecutionEngineTraceMarkers.Pop();
        gcSuspendEEMarker.Duration = TimeSpan.FromMilliseconds(obj.TimeStampRelativeMSec) - gcSuspendEEMarker.StartTime.Value;
        threadState.Thread.Markers.Add(gcSuspendEEMarker);
    }

    private void ClrOnGCStop(GCEndTraceData obj)
    {
        var threadState = GetThreadSamplingState(obj, (ulong)obj.ThreadID);
        if (threadState.PendingGCEvents.Count == 0) return;
        var gcEvent = threadState.PendingGCEvents.Pop();
        gcEvent.Duration = TimeSpan.FromMilliseconds(obj.TimeStampRelativeMSec) - gcEvent.StartTime.Value;
        threadState.Thread.Markers.Add(gcEvent);
    }

    private void ClrOnGCStart(GCStartTraceData gcStart)
    {
        var gcEvent = new GCTraceMarker
        {
            StartTime = UTimeSpan.FromMilliseconds(gcStart.TimeStampRelativeMSec),
            Reason = gcStart.Reason.ToString(),
            Count = gcStart.Count,
            Depth = gcStart.Depth,
            GCType = gcStart.Type.ToString()
        };
        GetThreadSamplingState(gcStart, (ulong)gcStart.ThreadID).PendingGCEvents.Push(gcEvent);
    }

    private void ClrOnGCAllocationTick(GCAllocationTickTraceData allocationTick)
    {
        var allocationTickEvent = new GCAllocationTickTraceMarker
        {
            StartTime = UTimeSpan.FromMilliseconds(allocationTick.TimeStampRelativeMSec),
            AllocationAmount = allocationTick.AllocationAmount,
            AllocationKind = allocationTick.AllocationKind switch
            {
                GCAllocationKind.Small => "Small",
                GCAllocationKind.Large => "Large",
                GCAllocationKind.Pinned => "Pinned",
                _ => "Unknown"
            },
            TypeName = allocationTick.TypeName,
            HeapIndex = allocationTick.HeapIndex
        };

        GetThreadSamplingState(allocationTick, (ulong)allocationTick.ThreadID).Thread.Markers.Add(allocationTickEvent);
    }

    private void ClrOnGCHeapStats(GCHeapStatsTraceData evt)
    {
        var threadState = GetThreadSamplingState(evt, (ulong)evt.ThreadID);
        var gcHeapStats = new GCHeapStatsTraceMarker()
        {
            StartTime = UTimeSpan.FromMilliseconds(evt.TimeStampRelativeMSec),
            TotalHeapSize = evt.TotalHeapSize,
            TotalPromoted = evt.TotalPromoted,
            GenerationSize0 = evt.GenerationSize0,
            TotalPromotedSize0 = evt.TotalPromotedSize0,
            GenerationSize1 = evt.GenerationSize1,
            TotalPromotedSize1 = evt.TotalPromotedSize1,
            GenerationSize2 = evt.GenerationSize2,
            TotalPromotedSize2 = evt.TotalPromotedSize2,
            GenerationSize3 = evt.GenerationSize3,
            TotalPromotedSize3 = evt.TotalPromotedSize3,
            GenerationSize4 = evt.GenerationSize4,
            TotalPromotedSize4 = evt.TotalPromotedSize4,
            FinalizationPromotedSize = evt.FinalizationPromotedSize,
            FinalizationPromotedCount = evt.FinalizationPromotedCount,
            PinnedObjectCount = evt.PinnedObjectCount,
            SinkBlockCount = evt.SinkBlockCount,
            GCHandleCount = evt.GCHandleCount
        };
        threadState.Thread.Markers.Add(gcHeapStats);
    }

    private void ClrOnMethodJittingStarted(MethodJittingStartedTraceData methodJittingStarted)
    {
        var threadState = GetThreadSamplingState(methodJittingStarted, (ulong)methodJittingStarted.ThreadID);

        var signature = methodJittingStarted.MethodSignature;
        var indexOfParent = signature.IndexOf('(');
        if (indexOfParent >= 0)
        {
            signature = signature.Substring(indexOfParent);
        }

        var jitCompileMarker = new JitCompileTraceMarker()
        {
            StartTime = UTimeSpan.FromMilliseconds(methodJittingStarted.TimeStampRelativeMSec),
            FullName = $"{methodJittingStarted.MethodNamespace}.{methodJittingStarted.MethodName}{signature}",
            MethodNamespace = methodJittingStarted.MethodNamespace,
            MethodName = methodJittingStarted.MethodName,
            MethodSignature = methodJittingStarted.MethodSignature,
            MethodILSize = methodJittingStarted.MethodILSize
        };

        threadState.PendingJitCompiles[methodJittingStarted.MethodID] = jitCompileMarker;
    }

    private void ProcessMethodILToNativeMap(MethodILToNativeMapTraceData obj)
    {
        var managedMethods = GetProcessState(obj).CurrentProcess!.ManagedMethods;
        if (!managedMethods.TryFindMethodById(obj.MethodID, out var method))
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
        // Log Jit Marker
        var threadState = GetThreadSamplingState(method, (ulong)method.ThreadID);
        if (threadState.PendingJitCompiles.TryGetValue(method.MethodID, out var jitCompileMarker))
        {
            jitCompileMarker.Duration = TimeSpan.FromMilliseconds(method.TimeStampRelativeMSec) -
                                                     jitCompileMarker.StartTime.Value;
            threadState.Thread.Markers.Add(jitCompileMarker);
            threadState.PendingJitCompiles.Remove(method.MethodID);
        }

        var managedMethods = GetProcessState(method).CurrentProcess!.ManagedMethods;
        managedMethods.GetOrCreateManagedMethod(method.ThreadID, method.ModuleID, method.MethodID, method.MethodNamespace, method.MethodName, method.MethodSignature, method.MethodToken, method.MethodFlags, method.MethodStartAddress, (ulong)method.MethodSize);
    }

    private void ProcessModuleLoadUnload(ModuleLoadUnloadTraceData data, bool isLoad, bool isDCStartStop)
    {
        var modules = GetProcessState(data).CurrentProcess!.Modules;
        var module = modules.GetOrCreateManagedModule(data.ModuleID, data.AssemblyID, data.ModuleILPath);

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
            var modules = GetProcessState(evt).CurrentProcess!.Modules;
            var module = modules.GetOrCreateNativeModule(evt.LoadAddress, evt.Size, evt.ModulePath);

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
        var process = GetProcessState(callstackTraceEvent).CurrentProcess!;
        GetThreadSamplingState(callstackTraceEvent, callstackTraceEvent.FrameThreadId).RecordStack(process, callstackTraceEvent);
    }

    private void SamplerParserOnEventNativeThreadStart(UltraNativeThreadStartTraceEvent obj)
    {
        var thread = GetThreadSamplingState(obj, obj.FrameThreadId).Thread;
        thread.StartTime = UTimeSpan.FromMilliseconds(obj.TimeStampRelativeMSec);
        thread.Name = obj.ThreadName;
    }

    private void SamplerParserOnEventNativeThreadStop(UltraNativeThreadStopTraceEvent obj)
    {
        var thread = GetThreadSamplingState(obj, obj.FrameThreadId).Thread;
        thread.StopTime = UTimeSpan.FromMilliseconds(obj.TimeStampRelativeMSec);
    }

    public UTraceSession Run()
    {
        // Run CLR if available
        _clrEventSource?.Process();

        foreach (var process in _session.Processes)
        {
            process.ManagedMethods.SortMethodAddressRanges();
        }

        // Run sampler before CLR
        _samplerEventSource.Process();

        _session.NumberOfProcessors = _samplerEventSource.NumberOfProcessors;
        _session.StartTime = _samplerEventSource.SessionStartTime;
        _session.Duration = _samplerEventSource.SessionDuration;
        _session.CpuSpeedMHz = _samplerEventSource.CpuSpeedMHz;

        return _session;
    }

    private void SamplerProcessInfo(TraceEvent obj)
    {
        var osInformation = obj.PayloadByName("OSInformation") as string;
        var archInformation = obj.PayloadByName("ArchInformation") as string;
        //Console.WriteLine(osInformation);
    }

    private ProcessState GetProcessState(TraceEvent evt)
    {
        var processID = evt.ProcessID;
        if (_currentProcessState is not null && _currentProcessState.ProcessID == processID)
        {
            return _currentProcessState;
        }
        if (!_processes.TryGetValue(processID, out var state))
        {
            state = new ProcessState()
            {
                ProcessID = processID,
            };
            _processes.Add(processID, state);

            var process = new UTraceProcess()
            {
                ProcessID = evt.ProcessID
            };
            state.SetCurrentProcess(process);
            _session.Processes.Add(process);
        }

        _currentProcessState = state;
        return state;
    }

    private ThreadSamplerState GetThreadSamplingState(TraceEvent evt, ulong threadID)
    {
        return GetProcessState(evt).GetThreadSamplingState(threadID);
    }

    private class ProcessState
    {
        public required int ProcessID;

        private UTraceProcess? _process;
        private UnsafeDictionary<ulong, ThreadSamplerState> _threadSamplingStates = new();

        public void SetCurrentProcess(UTraceProcess process)
        {
            if (_process != process)
            {
                _process = process;
                _threadSamplingStates.Clear();
            }
        }

        public UTraceProcess? CurrentProcess => _process;

        public ThreadSamplerState GetThreadSamplingState(ulong threadID)
        {
            if (!_threadSamplingStates.TryGetValue(threadID, out var threadSamplingState))
            {
                var thread = _process!.Threads.GetOrCreateThread(threadID);
                threadSamplingState = new(thread);
                _threadSamplingStates.Add(threadID, threadSamplingState);
            }
            return threadSamplingState;
        }
    }

    private class ThreadSamplerState
    {
        private readonly UCodeAddressIndex[] _previousFrame = new UCodeAddressIndex[63]; // 64 - 1 as in the sampler, the first index is used for the count
        private UnsafeList<UCodeAddressIndex> _callStack = new(1024);
        private readonly UTraceThread _thread;
        private TimeSpan _lastSampleTime;

        public UnsafeList<GCTraceMarker> PendingGCEvents = new();

        public UnsafeList<GCSuspendExecutionEngineTraceMarker> PendingGCSuspendExecutionEngineTraceMarkers = new();

        public UnsafeList<GCRestartExecutionEngineTraceMarker> PendingGCRestartExecutionEngineTraceMarkers = new();

        public UnsafeDictionary<long, JitCompileTraceMarker> PendingJitCompiles = new();

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

            var cpuTime = _lastSampleTime == TimeSpan.Zero || evt.ThreadState != UltraSamplerThreadState.Running ? 0.0 : evt.TimeStampRelativeMSec - _lastSampleTime.TotalMilliseconds;
            _thread.Samples.Add(new(callStackIndex, UTimeSpan.FromMilliseconds(evt.TimeStampRelativeMSec), UTimeSpan.FromMilliseconds(cpuTime)));
            _lastSampleTime = TimeSpan.FromMilliseconds(evt.TimeStampRelativeMSec);
        }
    }
}
