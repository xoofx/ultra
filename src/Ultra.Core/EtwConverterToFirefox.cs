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

public class EtwConverterToFirefox : IDisposable
{
    private readonly Dictionary<ModuleFileIndex, int> _mapModuleFileIndexToFirefox;
    private readonly HashSet<ModuleFileIndex> _setManagedModules;
    private readonly Dictionary<CallStackIndex, int> _mapCallStackIndexToFirefox;
    private readonly Dictionary<CodeAddressIndex, int> _mapCodeAddressIndexToFirefox;
    private readonly Dictionary<CodeAddressIndex, int> _mapCodeAddressIndexToMethodIndexFirefox;
    private readonly Dictionary<MethodIndex, int> _mapMethodIndexToFirefox;
    private readonly Dictionary<string, int> _mapStringToFirefox;
    private SymbolReader? _symbolReader;
    private ETWTraceEventSource _etl;
    private ModuleFileIndex _clrJitModuleIndex = ModuleFileIndex.Invalid;
    private ModuleFileIndex _coreClrModuleIndex = ModuleFileIndex.Invalid;

    public const int CategoryOther = 0;
    public const int CategoryKernel = 1;
    public const int CategoryNative = 2;
    public const int CategoryManaged = 3;
    public const int CategoryGC = 4;
    public const int CategoryJit = 5;
    public const int CategoryClr = 6;

    public EtwConverterToFirefox()
    {
        _mapModuleFileIndexToFirefox = new();
        _mapCallStackIndexToFirefox = new();
        _mapCodeAddressIndexToFirefox = new();
        _mapCodeAddressIndexToMethodIndexFirefox = new Dictionary<CodeAddressIndex, int>();
        _mapMethodIndexToFirefox = new();
        _mapStringToFirefox = new Dictionary<string, int>(StringComparer.Ordinal);
        _setManagedModules = new HashSet<ModuleFileIndex>();
    }
    
    public FirefoxProfiler.Profile Convert(string traceFilePath, List<int> processIds, EtwUltraProfilerOptions options)
    {
        const double MinimumCpuTimeBeforeThreadIsVisible = 10.0;

        _etl = new ETWTraceEventSource(traceFilePath);
        
        using var log = TraceLog.OpenOrConvert(traceFilePath);

        // Console.Out
        var symbolPath = options.GetCachedSymbolPath();
        var symbolPathText = symbolPath.ToString();

        _symbolReader = new SymbolReader(TextWriter.Null, symbolPathText);
        _symbolReader.Options = SymbolReaderOptions.None;
        _symbolReader.SecurityCheck = (pdbPath) => true;

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
                Platform = $"{log.OSName} {log.OSVersion} {log.OSBuild}",
                Oscpu = $"{log.OSName} {log.OSVersion} {log.OSBuild}",
                LogicalCPUs = log.NumberOfProcessors,
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
                Interval = log.SampleProfileInterval.TotalMilliseconds,
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
                ]
            }
        };

        profile.Meta.Abi = RuntimeInformation.RuntimeIdentifier;
        profile.Meta.MarkerSchema.Add(JitCompileEvent.Schema());
        profile.Meta.MarkerSchema.Add(GCEvent.Schema());
        profile.Meta.MarkerSchema.Add(GCHeapStatsEvent.Schema());
        profile.Meta.MarkerSchema.Add(GCAllocationTickEvent.Schema());
        profile.Meta.MarkerSchema.Add(GCSuspendExecutionEngineEvent.Schema());
        profile.Meta.MarkerSchema.Add(GCRestartExecutionEngineEvent.Schema());

        // MSNT_SystemTrace/Image/KernelBase - ThreadID="-1" ProcessorNumber="9" ImageBase="0xfffff80074000000" 
        
        // We don't have access to physical CPUs
        //profile.Meta.PhysicalCPUs = Environment.ProcessorCount / 2;
        //profile.Meta.CPUName = ""; // TBD

        int profileThreadIndex = 0;

        foreach (var processId in processIds)
        {
            // Reset all maps and default values before processing a new process
            _mapModuleFileIndexToFirefox.Clear();
            _setManagedModules.Clear();
            _clrJitModuleIndex = ModuleFileIndex.Invalid;
            _coreClrModuleIndex = ModuleFileIndex.Invalid;

            var process = log.Processes.LastProcessWithID(processId);

            if (profile.Meta.Product == string.Empty)
            {
                profile.Meta.Product = process.Name;
            }

            var processStartTime = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
            var processEndTime = new DateTimeOffset(process.EndTime.ToUniversalTime()).ToUnixTimeMilliseconds();
            if (processStartTime < profile.Meta.StartTime)
            {
                profile.Meta.StartTime = processStartTime;
            }
            if (processEndTime > profile.Meta.EndTime)
            {
                profile.Meta.EndTime = processEndTime;
            }

            var profilingStartTime = process.StartTimeRelativeMsec;
            if (profilingStartTime < profile.Meta.ProfilingStartTime)
            {
                profile.Meta.ProfilingStartTime = profilingStartTime;
            }
            var profilingEndTime = process.EndTimeRelativeMsec;
            if (profilingEndTime > profile.Meta.ProfilingEndTime)
            {
                profile.Meta.ProfilingEndTime = profilingEndTime;
            }

            options.LogProgress?.Invoke($"Loading Modules for process {process.Name}");

            var allModules = process.LoadedModules.ToList();
            for (var i = 0; i < allModules.Count; i++)
            {
                var module = allModules[i];
                if (_mapModuleFileIndexToFirefox.ContainsKey(module.ModuleFile.ModuleFileIndex))
                {
                    continue; // Skip in case
                }

                options.LogStepProgress?.Invoke($"Loading Symbols [{i}/{allModules.Count}] for Module `{module.Name}`, ImageSize: {ByteSize.FromBytes(module.ModuleFile.ImageSize)}");

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

                log.CodeAddresses.LookupSymbolsForModule(_symbolReader, module.ModuleFile);
                _mapModuleFileIndexToFirefox.Add(module.ModuleFile.ModuleFileIndex, profile.Libs.Count);
                profile.Libs.Add(lib);

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

            Dictionary<long, (JitCompileEvent, double)> jitCompilePendingMethodId = new();


            List<(double, GCHeapStatsEvent)> gcHeapStatsEvents = new();

            // Sort threads by CPU time
            var threads = process.Threads.ToList();
            threads.Sort((a, b) => b.CPUMSec.CompareTo(a.CPUMSec));

            Stack<(double, GCSuspendExecutionEngineEvent)> gcSuspendEEEvents = new();
            Stack<double> gcRestartEEEvents = new();
            Stack<(double, GCEvent)> gcStartStopEvents = new();

            double maxCpuTime = threads.Count > 0 ? threads[0].CPUMSec : 0;
            int threadIndexWithMaxCpuTime = threads.Count > 0 ? profileThreadIndex : -1;

            var threadVisited = new HashSet<int>();

            // Add threads
            foreach (var thread in threads)
            {
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

                gcSuspendEEEvents.Clear();
                gcRestartEEEvents.Clear();

                var profileThread = new FirefoxProfiler.Thread
                {
                    Name = thread.ThreadInfo is not null ? $"{thread.ThreadInfo} ({thread.ThreadID})" : $"Thread ({thread.ThreadID})",
                    ProcessStartupTime = thread.StartTimeRelativeMSec,
                    RegisterTime = thread.StartTimeRelativeMSec,
                    ProcessShutdownTime = thread.EndTimeRelativeMSec,
                    UnregisterTime = thread.EndTimeRelativeMSec,
                    ProcessType = "default",
                    Pid = $"{process.ProcessID}",
                    Tid = $"{thread.ThreadID}",
                    ShowMarkersInTimeline = true
                };

                options.LogProgress?.Invoke($"Converting Events for Thread: {profileThread.Name}");

                var samples = profileThread.Samples;
                var markers = profileThread.Markers;

                samples.ThreadCPUDelta = new List<int?>();
                samples.TimeDeltas = new List<double>();
                samples.WeightType = "samples";

                const TraceEventID GCStartEventID = (TraceEventID)1;
                const TraceEventID GCStopEventID = (TraceEventID)2;
                const TraceEventID GCRestartEEStopEventID = (TraceEventID)3;
                const TraceEventID GCHeapStatsEventID = (TraceEventID)4;
                const TraceEventID GCCreateSegmentEventID = (TraceEventID)5;
                const TraceEventID GCFreeSegmentEventID = (TraceEventID)6;
                const TraceEventID GCRestartEEStartEventID = (TraceEventID)7;
                const TraceEventID GCSuspendEEStopEventID = (TraceEventID)8;
                const TraceEventID GCSuspendEEStartEventID = (TraceEventID)9;
                const TraceEventID GCAllocationTickEventID = (TraceEventID)10;

                double startTime = 0;
                int currentThread = -1;
                double switchTimeInMsec = 0.0;
                //double switchTimeOutMsec = 0.0;
                foreach (var evt in thread.EventsInThread)
                {
                    if (evt.Opcode != (TraceEventOpcode)46)
                    {
                        if (evt.Opcode == (TraceEventOpcode)0x24 && evt is CSwitchTraceData switchTraceData)
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
                                    FullName = $"{methodJittingStarted.MethodNamespace}.{methodJittingStarted.MethodName}{signature}",
                                    MethodILSize = methodJittingStarted.MethodILSize
                                };

                                jitCompilePendingMethodId[methodJittingStarted.MethodID] = (jitCompile, evt.TimeStampRelativeMSec);
                            }
                            else if (evt is MethodLoadUnloadTraceDataBase methodLoadUnloadVerbose)
                            {
                                if (jitCompilePendingMethodId.TryGetValue(methodLoadUnloadVerbose.MethodID, out var jitCompilePair))
                                {
                                    jitCompilePendingMethodId.Remove(methodLoadUnloadVerbose.MethodID);

                                    markers.StartTime.Add(jitCompilePair.Item2);
                                    markers.EndTime.Add(evt.TimeStampRelativeMSec);
                                    markers.Category.Add(CategoryJit);
                                    markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                                    markers.ThreadId.Add(profileThreadIndex);
                                    markers.Name.Add(GetFirefoxString("JitCompile", profileThread));
                                    markers.Data.Add(jitCompilePair.Item1);
                                    markers.Length++;
                                }
                            }
                            else if (evt is GCHeapStatsTraceData gcHeapStats)
                            {
                                markers.StartTime.Add(evt.TimeStampRelativeMSec);
                                markers.EndTime.Add(evt.TimeStampRelativeMSec);
                                markers.Category.Add(CategoryGC);
                                markers.Phase.Add(FirefoxProfiler.MarkerPhase.Instance);
                                markers.ThreadId.Add(profileThreadIndex);
                                markers.Name.Add(GetFirefoxString($"GCHeapStats", profileThread));

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
                                markers.Category.Add(CategoryGC);
                                markers.Phase.Add(FirefoxProfiler.MarkerPhase.Instance);
                                markers.ThreadId.Add(profileThreadIndex);
                                markers.Name.Add(GetFirefoxString($"GC Alloc ({thread.ThreadID})", profileThread));

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
                                    markers.Category.Add(CategoryGC);
                                    markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                                    markers.ThreadId.Add(profileThreadIndex);
                                    markers.Name.Add(GetFirefoxString($"GC Event", profileThread));
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
                                else if (evt.ID == GCSuspendEEStopEventID && evt is GCNoUserDataTraceData && gcSuspendEEEvents.Count > 0)
                                {
                                    var (gcSuspendEEEventStartTime, gcSuspendEEEvent) = gcSuspendEEEvents.Pop();

                                    markers.StartTime.Add(gcSuspendEEEventStartTime);
                                    markers.EndTime.Add(evt.TimeStampRelativeMSec);
                                    markers.Category.Add(CategoryGC);
                                    markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                                    markers.ThreadId.Add(profileThreadIndex);
                                    markers.Name.Add(GetFirefoxString($"GC Suspend EE", profileThread));
                                    markers.Data.Add(gcSuspendEEEvent);
                                    markers.Length++;
                                }
                                else if (evt.ID == GCRestartEEStartEventID && evt is GCNoUserDataTraceData)
                                {
                                    gcRestartEEEvents.Push(evt.TimeStampRelativeMSec);
                                }
                                else if (evt.ID == GCRestartEEStopEventID && evt is GCNoUserDataTraceData && gcRestartEEEvents.Count > 0)
                                {
                                    var gcRestartEEEventStartTime = gcRestartEEEvents.Pop();

                                    markers.StartTime.Add(gcRestartEEEventStartTime);
                                    markers.EndTime.Add(evt.TimeStampRelativeMSec);
                                    markers.Category.Add(CategoryGC);
                                    markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                                    markers.ThreadId.Add(profileThreadIndex);
                                    markers.Name.Add(GetFirefoxString($"GC Restart EE", profileThread));
                                    markers.Data.Add(null);
                                    markers.Length++;
                                }
                            }
                        }

                        continue;
                    }

                    if (evt.ProcessID != processId || evt.ThreadID != thread.ThreadID)
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
                    var firefoxCallStackIndex = ProcessCallStack(callStackIndex, log, profileThread);

                    var deltaTime = evt.TimeStampRelativeMSec - startTime;
                    samples.TimeDeltas.Add(deltaTime);
                    samples.Stack.Add(firefoxCallStackIndex);
                    var cpuDeltaMs = (long)((evt.TimeStampRelativeMSec - switchTimeInMsec) * 1_000_000.0);
                    if (cpuDeltaMs > 0)
                    {
                        samples.ThreadCPUDelta.Add((int)cpuDeltaMs);
                    }
                    else
                    {
                        samples.ThreadCPUDelta.Add(0);
                    }
                    switchTimeInMsec = evt.TimeStampRelativeMSec;
                    samples.Length++;
                    startTime = evt.TimeStampRelativeMSec;
                }

                profile.Threads.Add(profileThread);

                // Make visible threads in the UI that consume a minimum amount of CPU time
                if (thread.CPUMSec > MinimumCpuTimeBeforeThreadIsVisible)
                {
                    profile.Meta.InitialVisibleThreads.Add(profileThreadIndex);
                }

                // We will select by default the thread that has the maximum activity
                if (thread.CPUMSec > maxCpuTime)
                {
                    maxCpuTime = thread.CPUMSec;
                    threadIndexWithMaxCpuTime = profileThreadIndex;
                }

                profileThreadIndex++;
            }

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

                profile.Counters ??= new();
                profile.Counters.Add(gcHeapStatsCounter);

                long previousTotalHeapSize = 0;

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
                if (!profile.Meta.InitialVisibleThreads.Contains(threadIndexWithMaxCpuTime))
                {
                    profile.Meta.InitialVisibleThreads.Add(threadIndexWithMaxCpuTime);
                }

                profile.Meta.InitialSelectedThreads.Add(threadIndexWithMaxCpuTime);
            }
        }

        return profile;
    }

    private int ProcessCallStack(CallStackIndex callStackIndex, TraceLog log, FirefoxProfiler.Thread profileThread)
    {
        if (callStackIndex == CallStackIndex.Invalid) return -1;

        var parentCallStackIndex = log.CallStacks.Caller(callStackIndex);
        var fireFoxParentCallStackIndex = ProcessCallStack(parentCallStackIndex, log, profileThread);

        return GetFirefoxCallStackIndex(callStackIndex, fireFoxParentCallStackIndex, log, profileThread);
    }
    
    private int GetFirefoxString(string text, FirefoxProfiler.Thread profileThread)
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
    
    private int GetFirefoxMethodIndex(CodeAddressIndex codeAddressIndex, MethodIndex methodIndex, TraceLog log, FirefoxProfiler.Thread profileThread)
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
            funcTable.Name.Add(GetFirefoxString($"0x{log.CodeAddresses.Address(codeAddressIndex):X16}", profileThread));
            funcTable.IsJS.Add(false);
            funcTable.RelevantForJS.Add(false);
            funcTable.Resource.Add(-1);
            funcTable.FileName.Add(null);
            funcTable.LineNumber.Add(null);
            funcTable.ColumnNumber.Add(null);
        }
        else
        {
            var fullMethodName = log.CodeAddresses.Methods.FullMethodName(methodIndex) ?? $"0x{log.CodeAddresses.Address(codeAddressIndex):X16}";

            var firefoxMethodNameIndex = GetFirefoxString(fullMethodName, profileThread);
            funcTable.Name.Add(firefoxMethodNameIndex);
            funcTable.IsJS.Add(false);
            funcTable.RelevantForJS.Add(false);
            funcTable.FileName.Add(null); // TODO
            funcTable.LineNumber.Add(null);
            funcTable.ColumnNumber.Add(null);

            var moduleIndex = log.CodeAddresses.ModuleFileIndex(codeAddressIndex);
            if (moduleIndex != ModuleFileIndex.Invalid)
            {
                funcTable.Resource.Add(profileThread.ResourceTable.Length); // TODO
                var moduleName = Path.GetFileName(log.ModuleFiles[moduleIndex].FilePath);
                profileThread.ResourceTable.Name.Add(GetFirefoxString(moduleName, profileThread));
                profileThread.ResourceTable.Lib.Add(_mapModuleFileIndexToFirefox[moduleIndex]);
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

    private int GetFirefoxCallStackIndex(CallStackIndex callStackIndex, int firefoxParentCallStackIndex, TraceLog log, FirefoxProfiler.Thread profileThread)
    {
        if (_mapCallStackIndexToFirefox.TryGetValue(callStackIndex, out var index))
        {
            return index;
        }
        var stackTable = profileThread.StackTable;

        var firefoxCallStackIndex = stackTable.Length;
        _mapCallStackIndexToFirefox.Add(callStackIndex, firefoxCallStackIndex);
        
        var codeAddressIndex = log.CallStacks.CodeAddressIndex(callStackIndex);
        var frameTableIndex = GetFirefoxFrameTableIndex(codeAddressIndex, log, profileThread, out var category, out var subCategory);
        
        stackTable.Frame.Add(frameTableIndex);
        stackTable.Category.Add(category);
        stackTable.Subcategory.Add(subCategory);
        stackTable.Prefix.Add(firefoxParentCallStackIndex < 0 ? null : (int)firefoxParentCallStackIndex);
        stackTable.Length++;
        
        return firefoxCallStackIndex;
    }

    private int GetFirefoxFrameTableIndex(CodeAddressIndex codeAddressIndex, TraceLog log, FirefoxProfiler.Thread profileThread, out int category, out int subCategory)
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

        var module = log.CodeAddresses.ModuleFile(codeAddressIndex);
        var absoluteAddress = log.CodeAddresses.Address(codeAddressIndex);
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

        var methodIndex = log.CodeAddresses.MethodIndex(codeAddressIndex);
        var firefoxMethodIndex = GetFirefoxMethodIndex(codeAddressIndex, methodIndex, log, profileThread);

        if (methodIndex != MethodIndex.Invalid)
        {
            var nameIndex = profileThread.FuncTable.Name[firefoxMethodIndex];
            var fullMethodName = profileThread.StringArray[nameIndex];
            // Hack to distinguish GC methods
            var isGC = fullMethodName.StartsWith("WKS::gc", StringComparison.OrdinalIgnoreCase) || fullMethodName.StartsWith("SVR::gc", StringComparison.OrdinalIgnoreCase);
            if (isGC)
            {
                category = CategoryGC;
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

    public void Dispose()
    {
        _symbolReader?.Dispose();
        _etl.Dispose();
    }
}