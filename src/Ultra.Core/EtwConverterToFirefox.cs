// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.InteropServices;
using ByteSizeLib;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Ultra.Core.Markers;

namespace Ultra.Core;

/// <summary>
/// Converts an ETW trace file to a Firefox profile.
/// </summary>
public sealed class EtwConverterToFirefox : IDisposable
{
    private readonly Dictionary<ModuleFileIndex, int> _mapModuleFileIndexToFirefox;
    private readonly HashSet<ModuleFileIndex> _setManagedModules;
    private readonly Dictionary<CallStackIndex, int> _mapCallStackIndexToFirefox;
    private readonly Dictionary<CodeAddressIndex, int> _mapCodeAddressIndexToFirefox;
    private readonly Dictionary<CodeAddressIndex, int> _mapCodeAddressIndexToMethodIndexFirefox;
    private readonly Dictionary<MethodIndex, int> _mapMethodIndexToFirefox;
    private readonly Dictionary<string, int> _mapStringToFirefox;
    private readonly SymbolReader _symbolReader;
    private readonly ETWTraceEventSource _etl;
    private readonly TraceLog _traceLog;
    private ModuleFileIndex _clrJitModuleIndex = ModuleFileIndex.Invalid;
    private ModuleFileIndex _coreClrModuleIndex = ModuleFileIndex.Invalid;
    private int _profileThreadIndex;
    private readonly EtwUltraProfilerOptions _options;
    private readonly FirefoxProfiler.Profile _profile;

    /// <summary>
    /// A generic other category.
    /// </summary>
    public const int CategoryOther = 0;

    /// <summary>
    /// The kernel category.
    /// </summary>
    public const int CategoryKernel = 1;

    /// <summary>
    /// The native category.
    /// </summary>
    public const int CategoryNative = 2;

    /// <summary>
    /// The managed category.
    /// </summary>
    public const int CategoryManaged = 3;

    /// <summary>
    /// The GC category.
    /// </summary>
    public const int CategoryGc = 4;

    /// <summary>
    /// The JIT category.
    /// </summary>
    public const int CategoryJit = 5;

    /// <summary>
    /// The CLR category.
    /// </summary>
    public const int CategoryClr = 6;

    private EtwConverterToFirefox(string traceFilePath, EtwUltraProfilerOptions options)
    {
        _etl = new ETWTraceEventSource(traceFilePath);
        _traceLog = TraceLog.OpenOrConvert(traceFilePath);

        var symbolPath = options.GetCachedSymbolPath();
        var symbolPathText = symbolPath.ToString();

        _symbolReader = new SymbolReader(TextWriter.Null, symbolPathText);
        _symbolReader.Options = SymbolReaderOptions.None;
        _symbolReader.SecurityCheck = (pdbPath) => true;

        this._profile = CreateProfile();
        
        this._options = options;

        _mapModuleFileIndexToFirefox = new();
        _mapCallStackIndexToFirefox = new();
        _mapCodeAddressIndexToFirefox = new();
        _mapCodeAddressIndexToMethodIndexFirefox = new ();
        _mapMethodIndexToFirefox = new();
        _mapStringToFirefox = new(StringComparer.Ordinal);
        _setManagedModules = new();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _symbolReader.Dispose();
        _traceLog.Dispose();
        _etl.Dispose();
    }

    /// <summary>
    /// Converts an ETW trace file to a Firefox profile.
    /// </summary>
    /// <param name="traceFilePath">The ETW trace file to convert.</param>
    /// <param name="options">The options used for converting.</param>
    /// <param name="processIds">The list of process ids to extract from the ETL file.</param>
    /// <returns>The converted Firefox profile.</returns>
    public static FirefoxProfiler.Profile Convert(string traceFilePath, EtwUltraProfilerOptions options, List<int> processIds)
    {
        using var converter = new EtwConverterToFirefox(traceFilePath, options);
        return converter.Convert(processIds);
    }

    private FirefoxProfiler.Profile Convert(List<int> processIds)
    {
        // MSNT_SystemTrace/Image/KernelBase - ThreadID="-1" ProcessorNumber="9" ImageBase="0xfffff80074000000" 
        
        // We don't have access to physical CPUs
        //profile.Meta.PhysicalCPUs = Environment.ProcessorCount / 2;
        //profile.Meta.CPUName = ""; // TBD

        _profileThreadIndex = 0;

        foreach (var processId in processIds)
        {
            var process = _traceLog.Processes.LastProcessWithID(processId);

            ConvertProcess(process);
        }

        return _profile;
    }

    /// <summary>
    /// Converts an ETW trace process to a Firefox profile.
    /// </summary>
    /// <param name="process">The process to convert.</param>
    private void ConvertProcess(TraceProcess process)
    {
        if (_profile.Meta.Product == string.Empty)
        {
            _profile.Meta.Product = process.Name;
        }

        var processStartTime = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        var processEndTime = new DateTimeOffset(process.EndTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        if (processStartTime < _profile.Meta.StartTime)
        {
            _profile.Meta.StartTime = processStartTime;
        }
        if (processEndTime > _profile.Meta.EndTime)
        {
            _profile.Meta.EndTime = processEndTime;
        }

        var profilingStartTime = process.StartTimeRelativeMsec;
        if (profilingStartTime < _profile.Meta.ProfilingStartTime)
        {
            _profile.Meta.ProfilingStartTime = profilingStartTime;
        }
        var profilingEndTime = process.EndTimeRelativeMsec;
        if (profilingEndTime > _profile.Meta.ProfilingEndTime)
        {
            _profile.Meta.ProfilingEndTime = profilingEndTime;
        }

        LoadModules(process);

        List<(double, GCHeapStatsEvent)> gcHeapStatsEvents = new();
        Dictionary<long, (JitCompileEvent, double)> jitCompilePendingMethodId = new();

        // Sort threads by CPU time
        var threads = process.Threads.ToList();
        threads.Sort((a, b) => b.CPUMSec.CompareTo(a.CPUMSec));
        
        double maxCpuTime = threads.Count > 0 ? threads[0].CPUMSec : 0;
        int threadIndexWithMaxCpuTime = threads.Count > 0 ? _profileThreadIndex : -1;

        var threadVisited = new HashSet<int>();

        var processName = $"{process.Name} ({process.ProcessID})";

        // Add threads
        for (var threadIndex = 0; threadIndex < threads.Count; threadIndex++)
        {
            var thread = threads[threadIndex];
            // Skip threads that have already been visited
            // TODO: for some reasons we have some threads that are duplicated?
            if (!threadVisited.Add(thread.ThreadID))
            {
                continue;
            }

            _mapCallStackIndexToFirefox.Clear();
            _mapCodeAddressIndexToFirefox.Clear();
            _mapMethodIndexToFirefox.Clear();
            _mapStringToFirefox.Clear();
            _mapCodeAddressIndexToMethodIndexFirefox.Clear();

            Stack<(double, GCSuspendExecutionEngineEvent)> gcSuspendEEEvents = new();
            Stack<double> gcRestartEEEvents = new();
            Stack<(double, GCEvent)> gcStartStopEvents = new();

            var threadBaseName = thread.ThreadInfo is not null
                ? $"{thread.ThreadInfo} ({thread.ThreadID})"
                : $"Thread ({thread.ThreadID})";
            var threadName = $"{threadIndex} - {threadBaseName}";
                
            var profileThread = new FirefoxProfiler.Thread
            {
                Name = threadName,
                ProcessName = processName,
                ProcessStartupTime = thread.StartTimeRelativeMSec,
                RegisterTime = thread.StartTimeRelativeMSec,
                ProcessShutdownTime = thread.EndTimeRelativeMSec,
                UnregisterTime = thread.EndTimeRelativeMSec,
                ProcessType = "default",
                Pid = $"{process.ProcessID}",
                Tid = $"{thread.ThreadID}",
                ShowMarkersInTimeline = true
            };

            _options.LogProgress?.Invoke($"Converting Events for Thread: {profileThread.Name}");

            var samples = profileThread.Samples;
            var markers = profileThread.Markers;

            samples.ThreadCPUDelta = new List<int?>();
            samples.TimeDeltas = new List<double>();
            samples.WeightType = "samples";

            //const TraceEventID GCStartEventID = (TraceEventID) 1;
            //const TraceEventID GCStopEventID = (TraceEventID) 2;
            const TraceEventID GCRestartEEStopEventID = (TraceEventID) 3;
            //const TraceEventID GCHeapStatsEventID = (TraceEventID) 4;
            //const TraceEventID GCCreateSegmentEventID = (TraceEventID) 5;
            //const TraceEventID GCFreeSegmentEventID = (TraceEventID) 6;
            const TraceEventID GCRestartEEStartEventID = (TraceEventID) 7;
            const TraceEventID GCSuspendEEStopEventID = (TraceEventID) 8;
            //const TraceEventID GCSuspendEEStartEventID = (TraceEventID) 9;
            //const TraceEventID GCAllocationTickEventID = (TraceEventID) 10;

            double startTime = 0;
            double switchTimeInMsec = 0.0;
            //double switchTimeOutMsec = 0.0;
            foreach (var evt in thread.EventsInThread)
            {
                if (evt.Opcode != (TraceEventOpcode) 46)
                {
                    if (evt.Opcode == (TraceEventOpcode) 0x24 && evt is CSwitchTraceData switchTraceData)
                    {
                        if (evt.ThreadID == thread.ThreadID && switchTraceData.OldThreadID != thread.ThreadID)
                        {
                            // Old Thread -> This Thread
                            // Switch-in
                            switchTimeInMsec = evt.TimeStampRelativeMSec;
                        }
                        //else if (evt.ThreadID != thread.ThreadID && switchTraceData.OldThreadID == thread.ThreadID)
                        //{
                        //    // This Thread -> Other Thread
                        //    // Switch-out
                        //    switchTimeOutMsec = evt.TimeStampRelativeMSec;
                        //}
                    }

                    if (evt.ThreadID == thread.ThreadID)
                    {
                        if (evt is MethodJittingStartedTraceData methodJittingStarted)
                        {
                            var signature = methodJittingStarted.MethodSignature;
                            var indexOfParent = signature.IndexOf('(');
                            if (indexOfParent >= 0)
                            {
                                signature = signature.Substring(indexOfParent);
                            }

                            var jitCompile = new JitCompileEvent
                            {
                                FullName =
                                    $"{methodJittingStarted.MethodNamespace}.{methodJittingStarted.MethodName}{signature}",
                                MethodILSize = methodJittingStarted.MethodILSize
                            };

                            jitCompilePendingMethodId[methodJittingStarted.MethodID] =
                                (jitCompile, evt.TimeStampRelativeMSec);
                        }
                        else if (evt is MethodLoadUnloadTraceDataBase methodLoadUnloadVerbose)
                        {
                            if (jitCompilePendingMethodId.TryGetValue(methodLoadUnloadVerbose.MethodID,
                                    out var jitCompilePair))
                            {
                                jitCompilePendingMethodId.Remove(methodLoadUnloadVerbose.MethodID);

                                markers.StartTime.Add(jitCompilePair.Item2);
                                markers.EndTime.Add(evt.TimeStampRelativeMSec);
                                markers.Category.Add(CategoryJit);
                                markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                                markers.ThreadId.Add(_profileThreadIndex);
                                markers.Name.Add(GetOrCreateString("JitCompile", profileThread));
                                markers.Data.Add(jitCompilePair.Item1);
                                markers.Length++;
                            }
                        }
                        else if (evt is GCHeapStatsTraceData gcHeapStats)
                        {
                            markers.StartTime.Add(evt.TimeStampRelativeMSec);
                            markers.EndTime.Add(evt.TimeStampRelativeMSec);
                            markers.Category.Add(CategoryGc);
                            markers.Phase.Add(FirefoxProfiler.MarkerPhase.Instance);
                            markers.ThreadId.Add(_profileThreadIndex);
                            markers.Name.Add(GetOrCreateString($"GCHeapStats", profileThread));

                            var heapStatEvent = new GCHeapStatsEvent
                            {
                                TotalHeapSize = gcHeapStats.TotalHeapSize,
                                TotalPromoted = gcHeapStats.TotalPromoted,
                                GenerationSize0 = gcHeapStats.GenerationSize0,
                                TotalPromotedSize0 = gcHeapStats.TotalPromotedSize0,
                                GenerationSize1 = gcHeapStats.GenerationSize1,
                                TotalPromotedSize1 = gcHeapStats.TotalPromotedSize1,
                                GenerationSize2 = gcHeapStats.GenerationSize2,
                                TotalPromotedSize2 = gcHeapStats.TotalPromotedSize2,
                                GenerationSize3 = gcHeapStats.GenerationSize3,
                                TotalPromotedSize3 = gcHeapStats.TotalPromotedSize3,
                                GenerationSize4 = gcHeapStats.GenerationSize4,
                                TotalPromotedSize4 = gcHeapStats.TotalPromotedSize4,
                                FinalizationPromotedSize = gcHeapStats.FinalizationPromotedSize,
                                FinalizationPromotedCount = gcHeapStats.FinalizationPromotedCount,
                                PinnedObjectCount = gcHeapStats.PinnedObjectCount,
                                SinkBlockCount = gcHeapStats.SinkBlockCount,
                                GCHandleCount = gcHeapStats.GCHandleCount
                            };

                            gcHeapStatsEvents.Add((evt.TimeStampRelativeMSec, heapStatEvent));

                            markers.Data.Add(heapStatEvent);
                            markers.Length++;
                        }
                        else if (evt is GCAllocationTickTraceData allocationTick)
                        {
                            markers.StartTime.Add(evt.TimeStampRelativeMSec);
                            markers.EndTime.Add(evt.TimeStampRelativeMSec);
                            markers.Category.Add(CategoryGc);
                            markers.Phase.Add(FirefoxProfiler.MarkerPhase.Instance);
                            markers.ThreadId.Add(_profileThreadIndex);
                            markers.Name.Add(GetOrCreateString($"{threadIndex} - GC Alloc ({thread.ThreadID})", profileThread));

                            var allocationTickEvent = new GCAllocationTickEvent
                            {
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
                            markers.Data.Add(allocationTickEvent);
                            markers.Length++;
                        }
                        else if (evt.ProviderGuid == ClrTraceEventParser.ProviderGuid)
                        {
                            if (evt is GCStartTraceData gcStart)
                            {
                                var gcEvent = new GCEvent
                                {
                                    Reason = gcStart.Reason.ToString(),
                                    Count = gcStart.Count,
                                    Depth = gcStart.Depth,
                                    GCType = gcStart.Type.ToString()
                                };

                                gcStartStopEvents.Push((evt.TimeStampRelativeMSec, gcEvent));
                            }
                            else if (evt is GCEndTraceData gcEnd && gcStartStopEvents.Count > 0)
                            {
                                var (gcEventStartTime, gcEvent) = gcStartStopEvents.Pop();

                                markers.StartTime.Add(gcEventStartTime);
                                markers.EndTime.Add(evt.TimeStampRelativeMSec);
                                markers.Category.Add(CategoryGc);
                                markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                                markers.ThreadId.Add(_profileThreadIndex);
                                markers.Name.Add(GetOrCreateString($"GC Event", profileThread));
                                markers.Data.Add(gcEvent);
                                markers.Length++;
                            }
                            else if (evt is GCSuspendEETraceData gcSuspendEE)
                            {
                                var gcSuspendEEEvent = new GCSuspendExecutionEngineEvent
                                {
                                    Reason = gcSuspendEE.Reason.ToString(),
                                    Count = gcSuspendEE.Count
                                };

                                gcSuspendEEEvents.Push((evt.TimeStampRelativeMSec, gcSuspendEEEvent));
                            }
                            else if (evt.ID == GCSuspendEEStopEventID && evt is GCNoUserDataTraceData &&
                                     gcSuspendEEEvents.Count > 0)
                            {
                                var (gcSuspendEEEventStartTime, gcSuspendEEEvent) = gcSuspendEEEvents.Pop();

                                markers.StartTime.Add(gcSuspendEEEventStartTime);
                                markers.EndTime.Add(evt.TimeStampRelativeMSec);
                                markers.Category.Add(CategoryGc);
                                markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                                markers.ThreadId.Add(_profileThreadIndex);
                                markers.Name.Add(GetOrCreateString($"GC Suspend EE", profileThread));
                                markers.Data.Add(gcSuspendEEEvent);
                                markers.Length++;
                            }
                            else if (evt.ID == GCRestartEEStartEventID && evt is GCNoUserDataTraceData)
                            {
                                gcRestartEEEvents.Push(evt.TimeStampRelativeMSec);
                            }
                            else if (evt.ID == GCRestartEEStopEventID && evt is GCNoUserDataTraceData &&
                                     gcRestartEEEvents.Count > 0)
                            {
                                var gcRestartEEEventStartTime = gcRestartEEEvents.Pop();

                                markers.StartTime.Add(gcRestartEEEventStartTime);
                                markers.EndTime.Add(evt.TimeStampRelativeMSec);
                                markers.Category.Add(CategoryGc);
                                markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                                markers.ThreadId.Add(_profileThreadIndex);
                                markers.Name.Add(GetOrCreateString($"GC Restart EE", profileThread));
                                markers.Data.Add(null);
                                markers.Length++;
                            }
                        }
                    }

                    continue;
                }

                if (evt.ProcessID != process.ProcessID || evt.ThreadID != thread.ThreadID)
                {
                    continue;
                }

                //Console.WriteLine($"PERF {evt}");

                var callStackIndex = evt.CallStackIndex();
                if (callStackIndex == CallStackIndex.Invalid)
                {
                    continue;
                }

                // Add sample
                var firefoxCallStackIndex = ConvertCallStack(callStackIndex, profileThread);

                var deltaTime = evt.TimeStampRelativeMSec - startTime;
                samples.TimeDeltas.Add(deltaTime);
                samples.Stack.Add(firefoxCallStackIndex);
                var cpuDeltaMs = (long) ((evt.TimeStampRelativeMSec - switchTimeInMsec) * 1_000_000.0);
                if (cpuDeltaMs > 0)
                {
                    samples.ThreadCPUDelta.Add((int) cpuDeltaMs);
                }
                else
                {
                    samples.ThreadCPUDelta.Add(0);
                }

                switchTimeInMsec = evt.TimeStampRelativeMSec;
                samples.Length++;
                startTime = evt.TimeStampRelativeMSec;
            }

            _profile.Threads.Add(profileThread);

            // Make visible threads in the UI that consume a minimum amount of CPU time
            if (thread.CPUMSec > _options.MinimumCpuTimeBeforeThreadIsVisibleInMs)
            {
                _profile.Meta.InitialVisibleThreads!.Add(_profileThreadIndex);
            }

            // We will select by default the thread that has the maximum activity
            if (thread.CPUMSec > maxCpuTime)
            {
                maxCpuTime = thread.CPUMSec;
                threadIndexWithMaxCpuTime = _profileThreadIndex;
            }

            _profileThreadIndex++;
        }

        // If we have GCHeapStatsEvents, we can create a Memory track
        if (gcHeapStatsEvents.Count > 0)
        {
            gcHeapStatsEvents.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            var gcHeapStatsCounter = new FirefoxProfiler.Counter()
            {
                Name = "GCHeapStats",
                Category = "Memory", // Category must be Memory otherwise it won't be displayed
                Description = "GC Heap Stats",
                Color = FirefoxProfiler.ProfileColor.Orange, // Doesn't look like it is used
                Pid = $"{process.ProcessID}",
                MainThreadIndex = threadIndexWithMaxCpuTime,
            };

            //gcHeapStatsCounter.Samples.Number = new();
            gcHeapStatsCounter.Samples.Time = new();

            _profile.Counters ??= new();
            _profile.Counters.Add(gcHeapStatsCounter);

            long previousTotalHeapSize = 0;

            // Bug in Memory, they discard the first sample
            // and it is then not recording the first TotalHeapSize which is the initial value
            // So we force to create a dummy empty entry
            // https://github.com/firefox-devtools/profiler/blob/e9fe870f2a85b1c8771b1d671eb316bd1f5723ec/src/profile-logic/profile-data.js#L1732-L1753
            gcHeapStatsCounter.Samples.Time!.Add(0);
            gcHeapStatsCounter.Samples.Count.Add(0);
            gcHeapStatsCounter.Samples.Length++;
                
            foreach (var evt in gcHeapStatsEvents)
            {
                gcHeapStatsCounter.Samples.Time!.Add(evt.Item1);
                // The memory track is special and is assuming a delta
                var deltaMemory = evt.Item2.TotalHeapSize - previousTotalHeapSize;
                gcHeapStatsCounter.Samples.Count.Add(deltaMemory);
                gcHeapStatsCounter.Samples.Length++;
                previousTotalHeapSize = evt.Item2.TotalHeapSize;
            }
        }

        if (threads.Count > 0)
        {
            // Always make at least the first thread visible (that is taking most of the CPU time)
            if (!_profile.Meta.InitialVisibleThreads!.Contains(threadIndexWithMaxCpuTime))
            {
                _profile.Meta.InitialVisibleThreads.Add(threadIndexWithMaxCpuTime);
            }

            _profile.Meta.InitialSelectedThreads!.Add(threadIndexWithMaxCpuTime);
        }
    }

    /// <summary>
    /// Loads the modules - and symbols for a given process.
    /// </summary>
    /// <param name="process">The process to load the modules.</param>
    private void LoadModules(TraceProcess process)
    {
        _options.LogProgress?.Invoke($"Loading Modules for process {process.Name} ({process.ProcessID})");

        _setManagedModules.Clear();
        _clrJitModuleIndex = ModuleFileIndex.Invalid;
        _coreClrModuleIndex = ModuleFileIndex.Invalid;

        var allModules = process.LoadedModules.ToList();
        for (var i = 0; i < allModules.Count; i++)
        {
            var module = allModules[i];
            if (!_mapModuleFileIndexToFirefox.ContainsKey(module.ModuleFile.ModuleFileIndex))
            {
                _options.LogStepProgress?.Invoke($"Loading Symbols [{i}/{allModules.Count}] for Module `{module.Name}`, ImageSize: {ByteSize.FromBytes(module.ModuleFile.ImageSize)}");

                var lib = new FirefoxProfiler.Lib
                {
                    Name = module.Name,
                    AddressStart = module.ImageBase,
                    AddressEnd = module.ModuleFile.ImageEnd,
                    Path = module.ModuleFile.FilePath,
                    DebugPath = module.ModuleFile.PdbName,
                    DebugName = module.ModuleFile.PdbName,
                    BreakpadId = $"0x{module.ModuleID:X16}",
                    Arch = "x64" // TODO
                };

                _traceLog!.CodeAddresses.LookupSymbolsForModule(_symbolReader, module.ModuleFile);

                _mapModuleFileIndexToFirefox.Add(module.ModuleFile.ModuleFileIndex, _profile.Libs.Count);
                _profile.Libs.Add(lib);
            }

            var fileName = Path.GetFileName(module.FilePath);
            if (fileName.Equals("clrjit.dll", StringComparison.OrdinalIgnoreCase))
            {
                _clrJitModuleIndex = module.ModuleFile.ModuleFileIndex;
            }
            else if (fileName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))
            {
                _coreClrModuleIndex = module.ModuleFile.ModuleFileIndex;
            }

            if (module is TraceManagedModule managedModule)
            {
                _setManagedModules.Add(managedModule.ModuleFile.ModuleFileIndex);

                foreach (var otherModule in allModules.Where(x => x is not TraceManagedModule))
                {
                    if (string.Equals(managedModule.FilePath, otherModule.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        _setManagedModules.Add(otherModule.ModuleFile.ModuleFileIndex);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Converts an ETW call stack to a Firefox call stack.
    /// </summary>
    /// <param name="callStackIndex">The ETW callstack index to convert.</param>
    /// <param name="profileThread">The current Firefox thread.</param>
    /// <returns>The converted Firefox call stack index.</returns>
    private int ConvertCallStack(CallStackIndex callStackIndex, FirefoxProfiler.Thread profileThread)
    {
        if (callStackIndex == CallStackIndex.Invalid) return -1;

        var parentCallStackIndex = _traceLog.CallStacks.Caller(callStackIndex);
        var fireFoxParentCallStackIndex = ConvertCallStack(parentCallStackIndex, profileThread);

        return ConvertCallStack(callStackIndex, fireFoxParentCallStackIndex, profileThread);
    }

    /// <summary>
    /// Converts an ETW call stack to a Firefox call stack.
    /// </summary>
    /// <param name="callStackIndex">The ETW callstack index to convert.</param>
    /// <param name="firefoxParentCallStackIndex">The parent Firefox callstack index.</param>
    /// <param name="profileThread">The current Firefox thread.</param>
    /// <returns>The converted Firefox call stack index.</returns>
    private int ConvertCallStack(CallStackIndex callStackIndex, int firefoxParentCallStackIndex, FirefoxProfiler.Thread profileThread)
    {
        if (_mapCallStackIndexToFirefox.TryGetValue(callStackIndex, out var index))
        {
            return index;
        }
        var stackTable = profileThread.StackTable;

        var firefoxCallStackIndex = stackTable.Length;
        _mapCallStackIndexToFirefox.Add(callStackIndex, firefoxCallStackIndex);

        var codeAddressIndex = _traceLog.CallStacks.CodeAddressIndex(callStackIndex);
        var frameTableIndex = ConvertFrame(codeAddressIndex, profileThread, out var category, out var subCategory);

        stackTable.Frame.Add(frameTableIndex);
        stackTable.Category.Add(category);
        stackTable.Subcategory.Add(subCategory);
        stackTable.Prefix.Add(firefoxParentCallStackIndex < 0 ? null : (int)firefoxParentCallStackIndex);
        stackTable.Length++;

        return firefoxCallStackIndex;
    }

    /// <summary>
    /// Converts an ETW code address to a Firefox frame.
    /// </summary>
    /// <param name="codeAddressIndex">The ETW code address index.</param>
    /// <param name="profileThread">The current Firefox thread.</param>
    /// <param name="category">The category of the frame.</param>
    /// <param name="subCategory">The subcategory of the frame.</param>
    /// <returns></returns>
    private int ConvertFrame(CodeAddressIndex codeAddressIndex, FirefoxProfiler.Thread profileThread, out int category, out int subCategory)
    {
        var frameTable = profileThread.FrameTable;

        if (_mapCodeAddressIndexToFirefox.TryGetValue(codeAddressIndex, out var firefoxFrameTableIndex))
        {
            category = frameTable.Category[firefoxFrameTableIndex]!.Value;
            subCategory = frameTable.Subcategory[firefoxFrameTableIndex]!.Value;
            return firefoxFrameTableIndex;
        }

        firefoxFrameTableIndex = frameTable.Length;
        _mapCodeAddressIndexToFirefox.Add(codeAddressIndex, firefoxFrameTableIndex);

        var module = _traceLog.CodeAddresses.ModuleFile(codeAddressIndex);
        var absoluteAddress = _traceLog.CodeAddresses.Address(codeAddressIndex);
        var offsetIntoModule = module is not null ? (int)(absoluteAddress - module.ImageBase) : 0;

        // Address
        // InlineDepth
        // Category
        // Subcategory
        // Func
        // NativeSymbol
        // InnerWindowID
        // Implementation
        // Line
        // Column

        frameTable.Address.Add(offsetIntoModule);
        frameTable.InlineDepth.Add(0);

        bool isManaged = false;
        if (module is not null)
        {
            isManaged = _setManagedModules.Contains(module.ModuleFileIndex);
        }

        subCategory = 0;

        if (isManaged)
        {
            category = CategoryManaged;
        }
        else
        {
            bool isKernel = (absoluteAddress >> 56) == 0xFF;
            category = isKernel ? CategoryKernel : CategoryNative;

            if (module != null)
            {
                if (module.ModuleFileIndex == _clrJitModuleIndex)
                {
                    category = CategoryJit;
                }
                else if (module.ModuleFileIndex == _coreClrModuleIndex)
                {
                    category = CategoryClr;
                }
            }
        }

        var methodIndex = _traceLog.CodeAddresses.MethodIndex(codeAddressIndex);
        var firefoxMethodIndex = ConvertMethod(codeAddressIndex, methodIndex, profileThread);

        if (methodIndex != MethodIndex.Invalid)
        {
            var nameIndex = profileThread.FuncTable.Name[firefoxMethodIndex];
            var fullMethodName = profileThread.StringArray[nameIndex];
            // Hack to distinguish GC methods
            // https://github.com/dotnet/runtime/blob/af3393d3991b7aab608e514e4a4be3ae2bbafbf8/src/coreclr/gc/gc.cpp#L49-L53
            var isGC = fullMethodName.StartsWith("WKS::gc", StringComparison.OrdinalIgnoreCase) || fullMethodName.StartsWith("SVR::gc", StringComparison.OrdinalIgnoreCase);
            if (isGC)
            {
                category = CategoryGc;
            }
        }

        frameTable.Category.Add(category);
        frameTable.Subcategory.Add(subCategory);

        frameTable.Func.Add(firefoxMethodIndex);

        // Set other fields to null
        frameTable.NativeSymbol.Add(null);
        frameTable.InnerWindowID.Add(null);
        frameTable.Implementation.Add(null);

        //var sourceLine = log.CodeAddresses.GetSourceLine(_symbolReader, codeAddressIndex);
        //if (sourceLine != null)
        //{
        //    ft.Line.Add(sourceLine.LineNumber);
        //    ft.Column.Add(sourceLine.ColumnNumber);
        //}
        //else
        {
            frameTable.Line.Add(null);
            frameTable.Column.Add(null);
        }
        frameTable.Length++;

        return firefoxFrameTableIndex;
    }

    /// <summary>
    /// Converts an ETW method to a Firefox method.
    /// </summary>
    /// <param name="codeAddressIndex">The original code address.</param>
    /// <param name="methodIndex">The method index. Can be invalid.</param>
    /// <param name="profileThread">The current Firefox thread.</param>
    /// <returns>The converted Firefox method index.</returns>
    private int ConvertMethod(CodeAddressIndex codeAddressIndex, MethodIndex methodIndex, FirefoxProfiler.Thread profileThread)
    {
        var funcTable = profileThread.FuncTable;
        int firefoxMethodIndex;
        if (methodIndex == MethodIndex.Invalid)
        {
            if (_mapCodeAddressIndexToMethodIndexFirefox.TryGetValue(codeAddressIndex, out var index))
            {
                return index;
            }
            firefoxMethodIndex = funcTable.Length;
            _mapCodeAddressIndexToMethodIndexFirefox[codeAddressIndex] = firefoxMethodIndex;
        }
        else if (_mapMethodIndexToFirefox.TryGetValue(methodIndex, out var index))
        {
            return index;
        }
        else
        {
            firefoxMethodIndex = funcTable.Length;
            _mapMethodIndexToFirefox.Add(methodIndex, firefoxMethodIndex);
        }
        
        //public List<int> Name { get; }
        //public List<bool> IsJS { get; }
        //public List<bool> RelevantForJS { get; }
        //public List<int> Resource { get; }
        //public List<int?> FileName { get; }
        //public List<int?> LineNumber { get; }
        //public List<int?> ColumnNumber { get; }

        if (methodIndex == MethodIndex.Invalid)
        {
            funcTable.Name.Add(GetOrCreateString($"0x{_traceLog.CodeAddresses.Address(codeAddressIndex):X16}", profileThread));
            funcTable.IsJS.Add(false);
            funcTable.RelevantForJS.Add(false);
            funcTable.Resource.Add(-1);
            funcTable.FileName.Add(null);
            funcTable.LineNumber.Add(null);
            funcTable.ColumnNumber.Add(null);
        }
        else
        {
            var fullMethodName = _traceLog.CodeAddresses.Methods.FullMethodName(methodIndex) ?? $"0x{_traceLog.CodeAddresses.Address(codeAddressIndex):X16}";

            var firefoxMethodNameIndex = GetOrCreateString(fullMethodName, profileThread);
            funcTable.Name.Add(firefoxMethodNameIndex);
            funcTable.IsJS.Add(false);
            funcTable.RelevantForJS.Add(false);
            funcTable.FileName.Add(null); // TODO
            funcTable.LineNumber.Add(null);
            funcTable.ColumnNumber.Add(null);

            var moduleIndex = _traceLog.CodeAddresses.ModuleFileIndex(codeAddressIndex);
            if (moduleIndex != ModuleFileIndex.Invalid && _mapModuleFileIndexToFirefox.TryGetValue(moduleIndex, out var firefoxModuleIndex))
            {
                funcTable.Resource.Add(profileThread.ResourceTable.Length);

                var moduleName = Path.GetFileName(_traceLog.ModuleFiles[moduleIndex].FilePath);
                profileThread.ResourceTable.Name.Add(GetOrCreateString(moduleName, profileThread));
                profileThread.ResourceTable.Lib.Add(firefoxModuleIndex);
                profileThread.ResourceTable.Length++;
            }
            else
            {
                funcTable.Resource.Add(-1);
            }
        }

        funcTable.Length++;

        return firefoxMethodIndex;
    }
    
    /// <summary>
    /// Gets or creates a string for the specified Firefox profile thread.
    /// </summary>
    /// <param name="text">The string to create.</param>
    /// <param name="profileThread">The current Firefox thread to create the string in.</param>
    /// <returns>The index of the string in the Firefox profile thread.</returns>
    private int GetOrCreateString(string text, FirefoxProfiler.Thread profileThread)
    {
        if (_mapStringToFirefox.TryGetValue(text, out var index))
        {
            return index;
        }
        var firefoxStringIndex = profileThread.StringArray.Count;
        _mapStringToFirefox.Add(text, firefoxStringIndex);

        profileThread.StringArray.Add(text);
        return firefoxStringIndex;
    }

    /// <summary>
    /// Creates a new Firefox profile.
    /// </summary>
    /// <returns>A new Firefox profile.</returns>
    private FirefoxProfiler.Profile CreateProfile()
    {
        var profile = new FirefoxProfiler.Profile
        {
            Meta =
            {
                StartTime = double.MaxValue,
                EndTime = 0.0f,
                ProfilingStartTime = double.MaxValue,
                ProfilingEndTime = 0.0f,
                Version = 29,
                PreprocessedProfileVersion = 51,
                Product = string.Empty,
                InitialSelectedThreads = [],
                Platform = $"{_traceLog.OSName} {_traceLog.OSVersion} {_traceLog.OSBuild}",
                Oscpu = $"{_traceLog.OSName} {_traceLog.OSVersion} {_traceLog.OSBuild}",
                LogicalCPUs = _traceLog.NumberOfProcessors,
                DoesNotUseFrameImplementation = true,
                Symbolicated = true,
                SampleUnits = new FirefoxProfiler.SampleUnits
                {
                    Time = "ms",
                    EventDelay = "ms",
                    ThreadCPUDelta = "ns"
                },
                InitialVisibleThreads = [],
                Stackwalk = 1,
                Interval = _traceLog.SampleProfileInterval.TotalMilliseconds,
                Categories =
                [
                    new FirefoxProfiler.Category()
                    {
                        Name = "Other",
                        Color = FirefoxProfiler.ProfileColor.Grey,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                    new FirefoxProfiler.Category()
                    {
                        Name = "Kernel",
                        Color = FirefoxProfiler.ProfileColor.Orange,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                    new FirefoxProfiler.Category()
                    {
                        Name = "Native",
                        Color = FirefoxProfiler.ProfileColor.Blue,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                    new FirefoxProfiler.Category()
                    {
                        Name = ".NET",
                        Color = FirefoxProfiler.ProfileColor.Green,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                    new FirefoxProfiler.Category()
                    {
                        Name = ".NET GC",
                        Color = FirefoxProfiler.ProfileColor.Yellow,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                    new FirefoxProfiler.Category()
                    {
                        Name = ".NET JIT",
                        Color = FirefoxProfiler.ProfileColor.Purple,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                    new FirefoxProfiler.Category()
                    {
                        Name = ".NET CLR",
                        Color = FirefoxProfiler.ProfileColor.Grey,
                        Subcategories =
                        {
                            "Other",
                        }
                    },
                ],
                Abi = RuntimeInformation.RuntimeIdentifier,
                MarkerSchema =
                {
                    JitCompileEvent.Schema(),
                    GCEvent.Schema(),
                    GCHeapStatsEvent.Schema(),
                    GCAllocationTickEvent.Schema(),
                    GCSuspendExecutionEngineEvent.Schema(),
                    GCRestartExecutionEngineEvent.Schema(),
                }
            }
        };

        return profile;
    }
}