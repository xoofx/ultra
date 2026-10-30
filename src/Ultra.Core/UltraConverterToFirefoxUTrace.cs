// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.InteropServices;
using Ultra.Core.Linux;
using Ultra.Core.MachO;
using Ultra.Core.Markers;
using Ultra.Core.Model;
using Ultra.Core.Symbols;

namespace Ultra.Core;

/// <summary>
/// Converts a trace session model (<see cref="UTraceSession"/>) to a Firefox profile. The derived classes load the
/// session from their trace files (macOS nettrace files, Linux perf events + CLR nettrace) and resolve the native symbols.
/// </summary>
internal abstract class UltraConverterToFirefoxUTrace : UltraConverterToFirefox
{
    private readonly Dictionary<UCallStackIndex, int> _mapCallStackIndexToFirefox = new();
    private readonly Dictionary<UCodeAddressIndex, int> _mapCodeAddressIndexToFirefox = new();
    private readonly Dictionary<UTraceManagedMethod, int> _mapManagedMethodToFirefox = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<UTraceModuleFile, int> _mapModuleFileToResourceFirefox = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> _mapStringToFirefox = new(StringComparer.Ordinal);
    private readonly Dictionary<UTraceModuleFileIndex, int> _mapModuleFileIndexToFirefox = new();
    private readonly Dictionary<UTraceModuleFile, INativeSymbolResolver?> _mapModuleFileToSymbolResolver = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(UTraceModuleFileIndex, ulong), int> _mapNativeSymbolToFirefox = new();
    private readonly HashSet<string> _mangledNames = new(StringComparer.Ordinal);

    private UTraceModuleFileIndex _clrJitModuleIndex = UTraceModuleFileIndex.Invalid;
    private UTraceModuleFileIndex _coreClrModuleIndex = UTraceModuleFileIndex.Invalid;
    private int _profileThreadIndex;
    private int _gcAllocationTickNameIndex = -1;

    // Offset to apply to the CLR relative timestamps of the current process to realign them with the sample times
    private double _clrTimeOffsetInMs;

    private protected UltraConverterToFirefoxUTrace(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options) : base(traceFiles, options)
    {
    }

    /// <summary>
    /// Loads the session: processes, threads, samples, native modules, managed methods and markers.
    /// </summary>
    /// <param name="processIds">The processes to convert.</param>
    protected abstract UTraceSession LoadSession(List<int> processIds);

    /// <summary>
    /// Gets the offset in milliseconds to add to the CLR relative marker times of a process to get sample relative times.
    /// </summary>
    protected abstract double GetClrTimeOffsetInMs(UTraceProcess process);

    /// <summary>
    /// Creates the symbol resolver of a native module file, or null when its symbols are not available.
    /// </summary>
    protected abstract INativeSymbolResolver? CreateSymbolResolver(UTraceModuleFile moduleFile);

    /// <summary>
    /// Gets the Firefox lib architecture string of a process.
    /// </summary>
    protected virtual string GetLibArch(UTraceProcess process) => process.ProcessArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.X86 => "x86",
        _ => "arm64"
    };

    private protected override void ConvertImpl(List<int> processIds)
    {
        Options.LogProgress?.Invoke("Processing Trace Files");

        var session = LoadSession(processIds);

        ProfilerResult = CreateProfile(session);

        _profileThreadIndex = 0;

        foreach (var processId in processIds)
        {
            foreach (var process in session.Processes)
            {
                if (process.ProcessID == processId)
                {
                    ConvertProcess(session, process);
                    break;
                }
            }
        }

        DemangleNativeSymbols();
    }

    /// <summary>
    /// Demangles the collected C++ mangled names in all the threads (when c++filt is available).
    /// </summary>
    private void DemangleNativeSymbols()
    {
        if (_mangledNames.Count == 0)
        {
            return;
        }

        Options.LogProgress?.Invoke($"Demangling {_mangledNames.Count} native symbols");

        var demangled = CppDemangler.Demangle(_mangledNames);
        if (demangled is null)
        {
            return;
        }

        foreach (var thread in ProfilerResult.Threads)
        {
            var stringArray = thread.StringArray;
            for (var i = 0; i < stringArray.Count; i++)
            {
                if (demangled.TryGetValue(stringArray[i], out var demangledName))
                {
                    stringArray[i] = demangledName;
                }
            }
        }
    }

    private void ConvertProcess(UTraceSession session, UTraceProcess process)
    {
        var processName = GetProcessName(process);
        if (ProfilerResult.Meta.Product == string.Empty)
        {
            ProfilerResult.Meta.Product = processName;
        }

        if (ProfilerResult.Meta.Oscpu is null && !string.IsNullOrEmpty(process.OSDescription))
        {
            ProfilerResult.Meta.Platform = process.OSDescription;
            ProfilerResult.Meta.Oscpu = process.OSDescription;
        }

        // The runtime identifier recorded with the process wins over the one of the converting machine
        if (!string.IsNullOrEmpty(process.RuntimeIdentifier) && (string.IsNullOrEmpty(ProfilerResult.Meta.Abi) || ProfilerResult.Meta.Abi == RuntimeInformation.RuntimeIdentifier))
        {
            ProfilerResult.Meta.Abi = process.RuntimeIdentifier;
        }

        _clrTimeOffsetInMs = GetClrTimeOffsetInMs(process);

        LoadModules(process);

        var sessionDurationInMs = session.Duration.InMs;

        List<(double, GCHeapStatsTraceMarker)> gcHeapStatsMarkers = new();

        // Sort threads by CPU time (descending) - CPU time is the sum of the CPU time of all samples
        var threads = new List<(UTraceThread Thread, double CpuTimeInMs)>();
        foreach (var thread in process.Threads.Items)
        {
            double cpuTimeInMs = 0;
            foreach (var sample in thread.Samples.Items)
            {
                cpuTimeInMs += sample.CpuTime.InMs;
            }
            threads.Add((thread, cpuTimeInMs));
        }
        threads.Sort((a, b) => b.CpuTimeInMs.CompareTo(a.CpuTimeInMs));

        double maxCpuTime = threads.Count > 0 ? threads[0].CpuTimeInMs : 0;
        int threadIndexWithMaxCpuTime = threads.Count > 0 ? _profileThreadIndex : -1;

        var processNameWithId = $"{processName} ({process.ProcessID})";

        for (var threadIndex = 0; threadIndex < threads.Count; threadIndex++)
        {
            var (thread, cpuTimeInMs) = threads[threadIndex];

            _mapCallStackIndexToFirefox.Clear();
            _mapCodeAddressIndexToFirefox.Clear();
            _mapManagedMethodToFirefox.Clear();
            _mapModuleFileToResourceFirefox.Clear();
            _mapStringToFirefox.Clear();
            _mapNativeSymbolToFirefox.Clear();

            var threadBaseName = string.IsNullOrEmpty(thread.Name)
                ? $"Thread ({thread.ThreadID})"
                : $"{thread.Name} ({thread.ThreadID})";
            var threadName = $"{threadIndex} - {threadBaseName}";

            var startTimeInMs = thread.StartTime.InMs;
            var stopTimeInMs = thread.StopTime.Value > TimeSpan.Zero ? thread.StopTime.InMs : sessionDurationInMs;

            var profileThread = new FirefoxProfiler.Thread
            {
                Name = threadName,
                ProcessName = processNameWithId,
                ProcessStartupTime = 0,
                RegisterTime = startTimeInMs,
                ProcessShutdownTime = sessionDurationInMs,
                UnregisterTime = stopTimeInMs,
                ProcessType = "default",
                Pid = $"{process.ProcessID}",
                Tid = $"{thread.ThreadID}",
                ShowMarkersInTimeline = true
            };

            Options.LogProgress?.Invoke($"Converting Events for Thread: {profileThread.Name}");

            var samples = profileThread.Samples;

            samples.ThreadCPUDelta = new List<int?>();
            samples.TimeDeltas = new List<double>();
            samples.WeightType = "samples";

            // Convert samples
            double previousSampleTimeInMs = 0;
            foreach (var sample in thread.Samples.Items)
            {
                var firefoxCallStackIndex = ConvertCallStack(sample.CallStackIndex, process, profileThread);
                if (firefoxCallStackIndex < 0)
                {
                    continue;
                }

                var sampleTimeInMs = sample.Timestamp.InMs;
                samples.TimeDeltas.Add(sampleTimeInMs - previousSampleTimeInMs);
                samples.Stack.Add(firefoxCallStackIndex);
                var cpuDeltaInNs = (long)(sample.CpuTime.InMs * 1_000_000.0);
                samples.ThreadCPUDelta.Add(cpuDeltaInNs > 0 ? (int)Math.Min(cpuDeltaInNs, int.MaxValue) : 0);
                samples.Length++;
                previousSampleTimeInMs = sampleTimeInMs;
            }

            // Convert markers
            _gcAllocationTickNameIndex = -1;
            foreach (var marker in thread.Markers.Items)
            {
                ConvertMarker(marker, threadIndex, thread, profileThread, gcHeapStatsMarkers);
            }

            ProfilerResult.Threads.Add(profileThread);

            // Make visible threads in the UI that consume a minimum amount of CPU time
            var visible = cpuTimeInMs > Options.MinimumCpuTimeBeforeThreadIsVisibleInMs;
            if (visible)
            {
                ProfilerResult.Meta.InitialVisibleThreads!.Add(_profileThreadIndex);
            }

            // We will select by default the thread that has the maximum activity
            if (cpuTimeInMs > maxCpuTime)
            {
                maxCpuTime = cpuTimeInMs;
                threadIndexWithMaxCpuTime = _profileThreadIndex;
            }

            _profileThreadIndex++;

            // Hardware counters: one more track per counter, right after the thread, its samples weighted by their counts
            for (var counterIndex = 0; counterIndex < thread.CounterSamples.Count; counterIndex++)
            {
                var counter = thread.CounterSamples[counterIndex];
                if (counter.Count == 0) continue;
                ConvertCounterThread(process, thread, counter, counterIndex + 1, $"{threadIndex} - {threadBaseName} [{counter.Name}]", processNameWithId, startTimeInMs, stopTimeInMs, sessionDurationInMs, visible);
            }
        }

        // If we have GCHeapStats markers, we can create a Memory track
        if (gcHeapStatsMarkers.Count > 0)
        {
            gcHeapStatsMarkers.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            var gcHeapStatsCounter = new FirefoxProfiler.Counter()
            {
                Name = "GCHeapStats",
                Category = "Memory", // Category must be Memory otherwise it won't be displayed
                Description = "GC Heap Stats",
                Color = FirefoxProfiler.ProfileColor.Orange, // Doesn't look like it is used
                Pid = $"{process.ProcessID}",
                MainThreadIndex = threadIndexWithMaxCpuTime,
            };

            gcHeapStatsCounter.Samples.Time = new();

            ProfilerResult.Counters ??= new();
            ProfilerResult.Counters.Add(gcHeapStatsCounter);

            long previousTotalHeapSize = 0;

            // Bug in Memory, they discard the first sample
            // and it is then not recording the first TotalHeapSize which is the initial value
            // So we force to create a dummy empty entry
            // https://github.com/firefox-devtools/profiler/blob/e9fe870f2a85b1c8771b1d671eb316bd1f5723ec/src/profile-logic/profile-data.js#L1732-L1753
            gcHeapStatsCounter.Samples.Time!.Add(0);
            gcHeapStatsCounter.Samples.Count.Add(0);
            gcHeapStatsCounter.Samples.Length++;

            foreach (var (timeInMs, gcHeapStats) in gcHeapStatsMarkers)
            {
                gcHeapStatsCounter.Samples.Time!.Add(timeInMs);
                // The memory track is special and is assuming a delta
                var deltaMemory = gcHeapStats.TotalHeapSize - previousTotalHeapSize;
                gcHeapStatsCounter.Samples.Count.Add(deltaMemory);
                gcHeapStatsCounter.Samples.Length++;
                previousTotalHeapSize = gcHeapStats.TotalHeapSize;
            }
        }

        if (threads.Count > 0)
        {
            // Always make at least the first thread visible (that is taking most of the CPU time)
            if (!ProfilerResult.Meta.InitialVisibleThreads!.Contains(threadIndexWithMaxCpuTime))
            {
                ProfilerResult.Meta.InitialVisibleThreads.Add(threadIndexWithMaxCpuTime);
            }

            ProfilerResult.Meta.InitialSelectedThreads!.Add(threadIndexWithMaxCpuTime);
        }
    }

    /// <summary>
    /// Converts the samples of a hardware counter of a thread to a Firefox thread of its own (a track under the thread),
    /// with the count of each sample as its weight: the call tree then shows where the counter events happen.
    /// </summary>
    private void ConvertCounterThread(UTraceProcess process, UTraceThread thread, UTraceCounterSampleList counter, int counterOrdinal, string name, string processNameWithId, double startTimeInMs, double stopTimeInMs, double sessionDurationInMs, bool visible)
    {
        _mapCallStackIndexToFirefox.Clear();
        _mapCodeAddressIndexToFirefox.Clear();
        _mapManagedMethodToFirefox.Clear();
        _mapModuleFileToResourceFirefox.Clear();
        _mapStringToFirefox.Clear();
        _mapNativeSymbolToFirefox.Clear();

        var profileThread = new FirefoxProfiler.Thread
        {
            Name = name,
            ProcessName = processNameWithId,
            ProcessStartupTime = 0,
            RegisterTime = startTimeInMs,
            ProcessShutdownTime = sessionDurationInMs,
            UnregisterTime = stopTimeInMs,
            ProcessType = "default",
            Pid = $"{process.ProcessID}",
            // A distinct tid per track, the profiler UI keys some of its state on it
            Tid = $"{thread.ThreadID}.{counterOrdinal}",
            ShowMarkersInTimeline = false
        };

        Options.LogProgress?.Invoke($"Converting {counter.Name} samples for Thread: {profileThread.Name}");

        var samples = profileThread.Samples;
        samples.TimeDeltas = new List<double>();
        samples.Weight = new List<int>();
        samples.WeightType = "samples";

        double previousSampleTimeInMs = 0;
        foreach (var sample in counter.Items)
        {
            var firefoxCallStackIndex = ConvertCallStack(sample.CallStackIndex, process, profileThread);
            if (firefoxCallStackIndex < 0)
            {
                continue;
            }

            var sampleTimeInMs = sample.Timestamp.InMs;
            samples.TimeDeltas.Add(sampleTimeInMs - previousSampleTimeInMs);
            samples.Stack.Add(firefoxCallStackIndex);
            samples.Weight.Add((int)Math.Min(sample.Count, int.MaxValue));
            samples.Length++;
            previousSampleTimeInMs = sampleTimeInMs;
        }

        ProfilerResult.Threads.Add(profileThread);
        if (visible)
        {
            ProfilerResult.Meta.InitialVisibleThreads!.Add(_profileThreadIndex);
        }
        _profileThreadIndex++;
    }

    private static string GetProcessName(UTraceProcess process)
    {
        if (!string.IsNullOrEmpty(process.FilePath))
        {
            return Path.GetFileName(process.FilePath);
        }

        // The main executable is the only native module that is not a dylib
        foreach (var module in process.Modules.Items)
        {
            if (module is UTraceNativeModule nativeModule && !nativeModule.ModuleFile.FilePath.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
            {
                return nativeModule.ModuleFile.FileName;
            }
        }

        return $"Process ({process.ProcessID})";
    }

    /// <summary>
    /// Loads the native modules of a given process as Firefox libs.
    /// </summary>
    /// <param name="process">The process to load the modules.</param>
    private void LoadModules(UTraceProcess process)
    {
        Options.LogProgress?.Invoke($"Loading Modules for process {GetProcessName(process)} ({process.ProcessID})");

        _clrJitModuleIndex = UTraceModuleFileIndex.Invalid;
        _coreClrModuleIndex = UTraceModuleFileIndex.Invalid;

        var arch = GetLibArch(process);

        foreach (var module in process.Modules.Items)
        {
            if (module is not UTraceNativeModule nativeModule)
            {
                continue;
            }

            var moduleFile = nativeModule.ModuleFile;
            if (!_mapModuleFileIndexToFirefox.ContainsKey(moduleFile.Index))
            {
                var moduleName = moduleFile.FileName;

                var lib = new FirefoxProfiler.Lib
                {
                    Name = moduleName,
                    AddressStart = nativeModule.BaseAddress,
                    AddressEnd = nativeModule.BaseAddress + nativeModule.CodeSize.Value,
                    Path = moduleFile.FilePath,
                    DebugPath = moduleFile.SymbolFilePath ?? moduleFile.FilePath,
                    DebugName = moduleName,
                    BreakpadId = $"{moduleFile.SymbolUuid:N}0".ToUpperInvariant(),
                    Arch = arch
                };

                _mapModuleFileIndexToFirefox.Add(moduleFile.Index, ProfilerResult.Libs.Count);
                ProfilerResult.Libs.Add(lib);

                if (moduleName.StartsWith("libclrjit", StringComparison.OrdinalIgnoreCase))
                {
                    _clrJitModuleIndex = moduleFile.Index;
                }
                else if (moduleName.StartsWith("libcoreclr", StringComparison.OrdinalIgnoreCase))
                {
                    _coreClrModuleIndex = moduleFile.Index;
                }
            }
        }
    }

    /// <summary>
    /// Converts a model call stack to a Firefox call stack.
    /// </summary>
    /// <param name="callStackIndex">The model callstack index to convert.</param>
    /// <param name="process">The process owning the callstack.</param>
    /// <param name="profileThread">The current Firefox thread.</param>
    /// <returns>The converted Firefox call stack index.</returns>
    private int ConvertCallStack(UCallStackIndex callStackIndex, UTraceProcess process, FirefoxProfiler.Thread profileThread)
    {
        // Index 0 is the reserved root callstack (invalid code address)
        if (callStackIndex.Value <= 0) return -1;

        if (_mapCallStackIndexToFirefox.TryGetValue(callStackIndex, out var index))
        {
            return index;
        }

        var frame = process.CallStacks[callStackIndex];
        var firefoxParentCallStackIndex = ConvertCallStack(frame.ParentCallStackIndex, process, profileThread);

        var stackTable = profileThread.StackTable;
        var firefoxCallStackIndex = stackTable.Length;

        var frameTableIndex = ConvertFrame(frame.CodeAddressIndex, process, profileThread, out var category, out var subCategory);

        stackTable.Frame.Add(frameTableIndex);
        stackTable.Category.Add(category);
        stackTable.Subcategory.Add(subCategory);
        stackTable.Prefix.Add(firefoxParentCallStackIndex < 0 ? null : firefoxParentCallStackIndex);
        stackTable.Length++;

        _mapCallStackIndexToFirefox.Add(callStackIndex, firefoxCallStackIndex);

        return firefoxCallStackIndex;
    }

    /// <summary>
    /// Converts a model code address to a Firefox frame.
    /// </summary>
    /// <param name="codeAddressIndex">The model code address index.</param>
    /// <param name="process">The process owning the code address.</param>
    /// <param name="profileThread">The current Firefox thread.</param>
    /// <param name="category">The category of the frame.</param>
    /// <param name="subCategory">The subcategory of the frame.</param>
    /// <returns>The converted Firefox frame index.</returns>
    private int ConvertFrame(UCodeAddressIndex codeAddressIndex, UTraceProcess process, FirefoxProfiler.Thread profileThread, out int category, out int subCategory)
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

        var address = process.CodeAddresses[codeAddressIndex];

        subCategory = 0;
        int firefoxFuncIndex;
        int frameAddress = -1;

        // The managed method was resolved when the sample was recorded (see UltraEventProcessor), as the
        // address alone is not enough to identify a collectible method whose code block has been recycled.
        var managedMethodIndex = process.CodeAddresses.GetManagedMethodIndex(codeAddressIndex);
        if (managedMethodIndex >= 0)
        {
            category = CategoryManaged;
            firefoxFuncIndex = ConvertManagedMethod(process.ManagedMethods[managedMethodIndex], process, profileThread);
            // JIT'd code has no image to be relative to: the offset in the method's code, so that the hot spots inside a
            // function can be told apart (the ETW converter gives the offset in the module image for the same purpose)
            if (process.ManagedMethods.TryFindMethodCodeStart(address, managedMethodIndex, out var codeStart) && address >= codeStart)
            {
                frameAddress = (int)Math.Min((address - codeStart).Value, int.MaxValue);
            }
        }
        else if (process.Modules.TryFindNativeModuleByAddress(address, out var nativeModule))
        {
            category = CategoryNative;
            var moduleFileIndex = nativeModule.ModuleFile.Index;
            if (moduleFileIndex == _clrJitModuleIndex)
            {
                category = CategoryJit;
            }
            else if (moduleFileIndex == _coreClrModuleIndex)
            {
                category = CategoryClr;
            }
            else if (nativeModule.ModuleFile.FilePath == KernelSymbolResolver.ModulePath)
            {
                category = CategoryKernel;
            }

            frameAddress = (int)(address - nativeModule.BaseAddress).Value;
            firefoxFuncIndex = ConvertNativeAddress(address, nativeModule, profileThread, ref category);
        }
        else
        {
            // Code addresses not belonging to a native module nor to a JIT-ed managed method
            // (e.g. JIT stubs, code produced before the CLR rundown)
            category = CategoryOther;
            firefoxFuncIndex = ConvertUnknownAddress(address, profileThread);
        }

        frameTable.Address.Add(frameAddress);
        frameTable.InlineDepth.Add(0);
        frameTable.Category.Add(category);
        frameTable.Subcategory.Add(subCategory);
        frameTable.Func.Add(firefoxFuncIndex);
        frameTable.NativeSymbol.Add(null);
        frameTable.InnerWindowID.Add(null);
        frameTable.Implementation.Add(null);
        frameTable.Line.Add(null);
        frameTable.Column.Add(null);
        frameTable.Length++;

        return firefoxFrameTableIndex;
    }

    /// <summary>
    /// Converts a managed method to a Firefox func.
    /// </summary>
    private int ConvertManagedMethod(UTraceManagedMethod managedMethod, UTraceProcess process, FirefoxProfiler.Thread profileThread)
    {
        if (_mapManagedMethodToFirefox.TryGetValue(managedMethod, out var index))
        {
            return index;
        }

        var funcTable = profileThread.FuncTable;
        var firefoxFuncIndex = funcTable.Length;
        _mapManagedMethodToFirefox.Add(managedMethod, firefoxFuncIndex);

        funcTable.Name.Add(GetOrCreateString(managedMethod.FullName, profileThread));
        funcTable.IsJS.Add(false);
        funcTable.RelevantForJS.Add(false);
        funcTable.FileName.Add(null);
        funcTable.LineNumber.Add(null);
        funcTable.ColumnNumber.Add(null);

        if (process.Modules.TryGetManagedModule(managedMethod.ModuleID, out var managedModule))
        {
            funcTable.Resource.Add(GetOrCreateResource(managedModule.ModuleFile, profileThread));
        }
        else
        {
            funcTable.Resource.Add(-1);
        }

        funcTable.Length++;

        return firefoxFuncIndex;
    }

    /// <summary>
    /// Converts a native address to a Firefox func, using the symbols of the module when available.
    /// </summary>
    private int ConvertNativeAddress(UAddress address, UTraceNativeModule nativeModule, FirefoxProfiler.Thread profileThread, ref int category)
    {
        var moduleFile = nativeModule.ModuleFile;

        if (!_mapModuleFileToSymbolResolver.TryGetValue(moduleFile, out var symbolResolver))
        {
            symbolResolver = CreateSymbolResolver(moduleFile);
            _mapModuleFileToSymbolResolver.Add(moduleFile, symbolResolver);
        }

        string funcName;
        if (symbolResolver is not null && symbolResolver.TryResolve(nativeModule, address, out var symbolAddress, out funcName))
        {
            // Hack to distinguish GC methods
            // https://github.com/dotnet/runtime/blob/af3393d3991b7aab608e514e4a4be3ae2bbafbf8/src/coreclr/gc/gc.cpp#L49-L53
            // The mangled name of e.g. WKS::gc_heap::garbage_collect is __ZN3WKS7gc_heap15garbage_collectEi on macOS
            // and _ZN3WKS7gc_heap15garbage_collectEi on Linux
            if (moduleFile.Index == _coreClrModuleIndex)
            {
                var unprefixed = funcName.AsSpan().TrimStart('_');
                if (unprefixed.StartsWith("ZN3WKS", StringComparison.Ordinal) || unprefixed.StartsWith("ZN3SVR", StringComparison.Ordinal))
                {
                    category = CategoryGc;
                }
            }

            if (CppDemangler.IsMangled(funcName))
            {
                _mangledNames.Add(funcName);
            }

            // Dedupe funcs per symbol so that samples in the same function aggregate in the call tree
            if (_mapNativeSymbolToFirefox.TryGetValue((moduleFile.Index, symbolAddress), out var index))
            {
                return index;
            }
            _mapNativeSymbolToFirefox.Add((moduleFile.Index, symbolAddress), profileThread.FuncTable.Length);
        }
        else
        {
            var moduleName = moduleFile.FileName;
            funcName = $"{moduleName}!0x{address.Value:X16}";
        }

        var funcTable = profileThread.FuncTable;
        var firefoxFuncIndex = funcTable.Length;

        funcTable.Name.Add(GetOrCreateString(funcName, profileThread));
        funcTable.IsJS.Add(false);
        funcTable.RelevantForJS.Add(false);
        funcTable.Resource.Add(GetOrCreateResource(moduleFile, profileThread));
        funcTable.FileName.Add(null);
        funcTable.LineNumber.Add(null);
        funcTable.ColumnNumber.Add(null);
        funcTable.Length++;

        return firefoxFuncIndex;
    }

    /// <summary>
    /// Converts an address not belonging to any known module or managed method to a Firefox func.
    /// </summary>
    private int ConvertUnknownAddress(UAddress address, FirefoxProfiler.Thread profileThread)
    {
        var funcTable = profileThread.FuncTable;
        var firefoxFuncIndex = funcTable.Length;

        funcTable.Name.Add(GetOrCreateString($"0x{address.Value:X16}", profileThread));
        funcTable.IsJS.Add(false);
        funcTable.RelevantForJS.Add(false);
        funcTable.Resource.Add(-1);
        funcTable.FileName.Add(null);
        funcTable.LineNumber.Add(null);
        funcTable.ColumnNumber.Add(null);
        funcTable.Length++;

        return firefoxFuncIndex;
    }

    /// <summary>
    /// Gets or creates a Firefox resource for the specified module file.
    /// </summary>
    private int GetOrCreateResource(UTraceModuleFile moduleFile, FirefoxProfiler.Thread profileThread)
    {
        if (_mapModuleFileToResourceFirefox.TryGetValue(moduleFile, out var index))
        {
            return index;
        }

        var resourceTable = profileThread.ResourceTable;
        var resourceIndex = resourceTable.Length;
        _mapModuleFileToResourceFirefox.Add(moduleFile, resourceIndex);

        var moduleName = moduleFile.FileName;
        resourceTable.Name.Add(GetOrCreateString(moduleName, profileThread));
        resourceTable.Lib.Add(_mapModuleFileIndexToFirefox.TryGetValue(moduleFile.Index, out var firefoxLibIndex) ? firefoxLibIndex : null);
        resourceTable.Host.Add(null);
        resourceTable.Type.Add(1); // 1 = library https://github.com/firefox-devtools/profiler/blob/main/src/types/profile.js (resourceTypes)
        resourceTable.Length++;

        return resourceIndex;
    }

    /// <summary>
    /// Converts a model marker to a Firefox marker.
    /// </summary>
    private void ConvertMarker(UTraceMarker marker, int threadIndex, UTraceThread thread, FirefoxProfiler.Thread profileThread, List<(double, GCHeapStatsTraceMarker)> gcHeapStatsMarkers)
    {
        var markers = profileThread.Markers;

        var startTimeInMs = marker.StartTime.InMs + _clrTimeOffsetInMs;
        var endTimeInMs = startTimeInMs + marker.Duration.InMs;

        switch (marker)
        {
            case JitCompileTraceMarker jitCompile:
                markers.StartTime.Add(startTimeInMs);
                markers.EndTime.Add(endTimeInMs);
                markers.Category.Add(CategoryJit);
                markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                markers.ThreadId.Add(_profileThreadIndex);
                markers.Name.Add(GetOrCreateString("JitCompile", profileThread));
                markers.Data.Add(new JitCompileEvent
                {
                    FullName = jitCompile.FullName,
                    MethodILSize = jitCompile.MethodILSize
                });
                markers.Length++;
                break;

            case GCTraceMarker gc:
                markers.StartTime.Add(startTimeInMs);
                markers.EndTime.Add(endTimeInMs);
                markers.Category.Add(CategoryGc);
                markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                markers.ThreadId.Add(_profileThreadIndex);
                markers.Name.Add(GetOrCreateString("GC Event", profileThread));
                markers.Data.Add(new GCEvent
                {
                    Reason = gc.Reason ?? string.Empty,
                    Count = gc.Count,
                    Depth = gc.Depth,
                    GCType = gc.GCType ?? string.Empty
                });
                markers.Length++;
                break;

            case GCSuspendExecutionEngineTraceMarker gcSuspendEE:
                markers.StartTime.Add(startTimeInMs);
                markers.EndTime.Add(endTimeInMs);
                markers.Category.Add(CategoryGc);
                markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                markers.ThreadId.Add(_profileThreadIndex);
                markers.Name.Add(GetOrCreateString("GC Suspend EE", profileThread));
                markers.Data.Add(new GCSuspendExecutionEngineEvent
                {
                    Reason = gcSuspendEE.Reason ?? string.Empty,
                    Count = gcSuspendEE.Count
                });
                markers.Length++;
                break;

            case GCRestartExecutionEngineTraceMarker:
                markers.StartTime.Add(startTimeInMs);
                markers.EndTime.Add(endTimeInMs);
                markers.Category.Add(CategoryGc);
                markers.Phase.Add(FirefoxProfiler.MarkerPhase.Interval);
                markers.ThreadId.Add(_profileThreadIndex);
                markers.Name.Add(GetOrCreateString("GC Restart EE", profileThread));
                markers.Data.Add(null);
                markers.Length++;
                break;

            case GCAllocationTickTraceMarker allocationTick:
                markers.StartTime.Add(startTimeInMs);
                markers.EndTime.Add(startTimeInMs);
                markers.Category.Add(CategoryGc);
                markers.Phase.Add(FirefoxProfiler.MarkerPhase.Instance);
                markers.ThreadId.Add(_profileThreadIndex);
                if (_gcAllocationTickNameIndex < 0)
                {
                    _gcAllocationTickNameIndex = GetOrCreateString($"{threadIndex} - GC Alloc ({thread.ThreadID})", profileThread);
                }
                markers.Name.Add(_gcAllocationTickNameIndex);
                markers.Data.Add(new GCAllocationTickEvent
                {
                    AllocationAmount = allocationTick.AllocationAmount,
                    AllocationKind = allocationTick.AllocationKind,
                    TypeName = allocationTick.TypeName,
                    HeapIndex = allocationTick.HeapIndex
                });
                markers.Length++;
                break;

            case GCHeapStatsTraceMarker gcHeapStats:
                markers.StartTime.Add(startTimeInMs);
                markers.EndTime.Add(startTimeInMs);
                markers.Category.Add(CategoryGc);
                markers.Phase.Add(FirefoxProfiler.MarkerPhase.Instance);
                markers.ThreadId.Add(_profileThreadIndex);
                markers.Name.Add(GetOrCreateString("GCHeapStats", profileThread));
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
                markers.Data.Add(heapStatEvent);
                markers.Length++;

                gcHeapStatsMarkers.Add((startTimeInMs, gcHeapStats));
                break;
        }
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
    /// Creates a new Firefox profile from the trace session.
    /// </summary>
    /// <param name="session">The trace session.</param>
    /// <returns>A new Firefox profile.</returns>
    private FirefoxProfiler.Profile CreateProfile(UTraceSession session)
    {
        var sessionStartTime = new DateTimeOffset(session.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        var sessionDurationInMs = session.Duration.InMs;

        var profile = new FirefoxProfiler.Profile
        {
            Meta =
            {
                StartTime = sessionStartTime,
                EndTime = sessionStartTime + sessionDurationInMs,
                ProfilingStartTime = 0,
                ProfilingEndTime = sessionDurationInMs,
                Version = 29,
                PreprocessedProfileVersion = 51,
                Product = string.Empty,
                InitialSelectedThreads = [],
                // The number of processors reported by EventPipe is not always valid (e.g. 0xFFFF)
                LogicalCPUs = session.NumberOfProcessors > 0 && session.NumberOfProcessors <= 4096 ? session.NumberOfProcessors : null,
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
                Interval = Options.CpuSamplingIntervalInMs,
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
