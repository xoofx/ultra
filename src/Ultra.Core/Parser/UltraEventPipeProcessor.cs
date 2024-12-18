// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XenoAtom.Collections;

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
    private readonly List<NativeModule> _nativeModules = new();
    private readonly List<AddressRange> _nativeModuleAddressRanges = new();
    private readonly List<AddressRange> _sortedNativeModuleAddressRanges = new();

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
                    index = _nativeModules.Count;
                    _mapModuleNameToIndex.Add(evt.ModulePath, index);
                    _nativeModules.Add(new(evt.ModulePath, evt.Uuid));
                    _nativeModuleAddressRanges.Add(new(evt.LoadAddress, evt.LoadAddress + evt.Size, index));
                }
                else
                {
                    _nativeModuleAddressRanges[index] = new(evt.LoadAddress, evt.LoadAddress + evt.Size, index);
                }

                _sortedNativeModuleAddressRanges.Clear();
                _sortedNativeModuleAddressRanges.AddRange(_nativeModuleAddressRanges);

                // Always keep the list sorted because we resolve the address to the module while parsing the native callstacks
                CollectionsMarshal.AsSpan(_sortedNativeModuleAddressRanges).SortByRef(new AddressRangeComparer());
            }
        }

    }

    private void SamplerParserOnEventNativeCallstack(UltraNativeCallstackTraceEvent obj)
    {
        PrintCallStack(obj);
    }
    
    private void PrintCallStack(UltraNativeCallstackTraceEvent callstackTraceEvent)
    {
        var sortedNativeModuleAddressRanges = CollectionsMarshal.AsSpan(this._sortedNativeModuleAddressRanges);
        Console.WriteLine($"Thread: {callstackTraceEvent.FrameThreadId}, State: {callstackTraceEvent.ThreadState}, Cpu: {callstackTraceEvent.ThreadCpuUsage}, SameFrameCount: {callstackTraceEvent.PreviousFrameCount}, FrameCount: {callstackTraceEvent.FrameSize / sizeof(ulong)} ");
        var span = callstackTraceEvent.FrameAddresses;
        for (var i = 0; i < span.Length; i++)
        {
            var frame = span[i];
            var addressRangeIndex = FindAddressRange(sortedNativeModuleAddressRanges, frame);
            if (addressRangeIndex >= 0)
            {
                var addressRange = sortedNativeModuleAddressRanges[addressRangeIndex];
                var module = _nativeModules[addressRange.Index];
                Console.WriteLine($"  {module.ModulePath}+0x{frame - addressRange.BeginAddress:X} (Module: 0x{addressRange.BeginAddress:X} Address: 0x{frame:X})");
            }
            else
            {
                Console.WriteLine($"  0x{frame:X}");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindAddressRange(Span<AddressRange> ranges, ulong address)
    {
        var comparer = new ModuleAddressComparer(address);
        return ranges.BinarySearch(comparer);
    }

    public void Run()
    {
        // Run CLR if available
        _clrEventSource?.Process();

        // Run sampler before CLR
        _samplerEventSource.Process();
    }

    private readonly record struct AddressRange(ulong BeginAddress, ulong EndAddress, int Index)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(ulong address) => address >= BeginAddress && address < EndAddress;
    }

    private readonly record struct NativeModule(string ModulePath, Guid Uuid);
    
    private readonly record struct ModuleAddressComparer(ulong Address) : IComparable<AddressRange>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(AddressRange other)
        {
            return other.Contains(Address) ? 0 : Address.CompareTo(other.BeginAddress);
        }
    }

    private readonly record struct AddressRangeComparer : IComparerByRef<AddressRange>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool LessThan(in AddressRange left, in AddressRange right) => left.BeginAddress < right.BeginAddress;
    }
}