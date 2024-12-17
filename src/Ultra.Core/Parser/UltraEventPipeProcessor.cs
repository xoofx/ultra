// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Reflection;
using System.Runtime.InteropServices;

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
    private readonly Dictionary<string, int> _mapModuleNameToIndex = new ();
    private readonly List<ModuleAddress> _modules = new();
    private readonly List<ModuleAddress> _sortedModules = new();

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
    
    private void SamplerParserOnEventNativeModule(UltraNativeModuleTraceEvent evt)
    {
        if (evt.ModulePath is not null)
        {
            Console.WriteLine($"Module {evt.NativeModuleEventKind} Path: {evt.ModulePath}, LoadAddress: 0x{evt.LoadAddress:X}, Size: 0x{evt.Size:X}, Timestamp: {evt.TimestampUtc}, Uuid: {evt.Uuid}");

            if (evt.NativeModuleEventKind == UltraSamplerNativeModuleEventKind.Unloaded)
            {
                _mapModuleNameToIndex.Remove(evt.ModulePath);
            }
            else
            {
                if (!_mapModuleNameToIndex.TryGetValue(evt.ModulePath, out var index))
                {
                    index = _modules.Count;
                    _mapModuleNameToIndex.Add(evt.ModulePath, index);
                    _modules.Add(new(evt.ModulePath, evt.LoadAddress, evt.Size));
                }
                else
                {
                    _modules[index] = new(evt.ModulePath, evt.LoadAddress, evt.Size);
                }

                _sortedModules.Clear();
                _sortedModules.AddRange(_modules);

                // Always keep the list sorted
                _sortedModules.Sort(static (left, right) => left.Address.CompareTo(right.Address));
            }
        }

    }

    private void SamplerParserOnEventNativeCallstack(UltraNativeCallstackTraceEvent obj)
    {
        PrintCallStack(obj);
    }
    
    private void PrintCallStack(UltraNativeCallstackTraceEvent callstackTraceEvent)
    {
        var sortedModules = CollectionsMarshal.AsSpan(this._sortedModules);
        Console.WriteLine($"Thread: {callstackTraceEvent.FrameThreadId}, State: {callstackTraceEvent.ThreadState}, Cpu: {callstackTraceEvent.ThreadCpuUsage}, SameFrameCount: {callstackTraceEvent.PreviousFrameCount}, FrameCount: {callstackTraceEvent.FrameSize / sizeof(ulong)} ");
        var span = callstackTraceEvent.FrameAddresses;
        for (var i = 0; i < span.Length; i++)
        {
            var frame = span[i];
            var moduleIndex = FindModuleIndex(sortedModules, frame);
            if (moduleIndex != -1)
            {
                var module = sortedModules[moduleIndex];
                Console.WriteLine($"  {module.ModulePath}+0x{frame - module.Address:X} (Module: 0x{module.Address:X} Address: 0x{frame:X})");
            }
            else
            {
                Console.WriteLine($"  0x{frame:X}");
            }
        }
    }

    private static int FindModuleIndex(Span<ModuleAddress> modules, ulong address)
    {
        for (var i = 0; i < modules.Length; i++)
        {
            if (address >= modules[i].Address && address < modules[i].Address + modules[i].Size)
            {
                return i;
            }
        }

        return -1;
    }

    public void Run()
    {
        // Run CLR if available
        _clrEventSource?.Process();

        // Run sampler before CLR
        _samplerEventSource.Process();
    }


    private record struct ModuleAddress(string ModulePath, ulong Address, ulong Size);
}