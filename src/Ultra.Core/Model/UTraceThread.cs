// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XenoAtom.Collections;

namespace Ultra.Core.Model;

public class UTraceProcess
{
    public ulong ProcessID { get; set; }

    public string FilePath { get; set; } = string.Empty;

    public string CommandLine { get; set; } = string.Empty;

    public List<UTraceThread> Threads { get; } = new();

    public UTraceModuleList Modules { get; } = new();

    public UTraceManagedMethodList ManagedMethods { get; } = new();
}

public record UTraceThread(ulong ThreadID)
{
    public DateTime StartTime { get; set; }

    public DateTime EndTime { get; set; }

    public string VerboseName { get; set; } = string.Empty;
}

public class UTraceThreadList
{
    private readonly Dictionary<ulong, int> _mapThreadIDToIndex = new();
    private readonly List<UTraceThread> _threads = new();

    public ReadOnlySpan<UTraceThread> AsSpan => CollectionsMarshal.AsSpan(_threads);

    public UTraceThread GetOrCreateThread(ulong threadID)
    {
        if (_mapThreadIDToIndex.TryGetValue(threadID, out var index))
        {
            return _threads[index];
        }
        var thread = new UTraceThread(threadID);
        index = _threads.Count;
        _mapThreadIDToIndex.Add(threadID, index);
        _threads.Add(thread);
        return thread;
    }
}

public class UTraceManagedMethodList
{
    private readonly Dictionary<UAddress, int> _mapMethodAddressToMethodIndex = new();
    private readonly Dictionary<long, int> _mapManagedMethodIDToMethodIndex = new();
    private readonly List<UTraceManagedMethod> _methods = new();
    private readonly List<UAddressRange> _methodAddressRanges = new();

    public ReadOnlySpan<UTraceManagedMethod> AsSpan => CollectionsMarshal.AsSpan(_methods);

    public UTraceManagedMethod GetOrCreateManagedMethod(int threadID, long moduleID, long methodID, string methodNamespace, string methodName, string methodSignature, int methodToken, MethodFlags methodFlags, UAddress methodStartAddress, USize methodSize)
    {
        if (_mapManagedMethodIDToMethodIndex.TryGetValue(methodID, out var methodIndex))
        {
            return _methods[methodIndex];
        }
        var method = new UTraceManagedMethod(threadID, moduleID, methodID, methodNamespace, methodName, methodSignature, methodToken, methodFlags, methodStartAddress, methodSize);
        methodIndex = _methods.Count;
        _mapManagedMethodIDToMethodIndex.Add(methodID, methodIndex);
        _methods.Add(method);
        _methodAddressRanges.Add(new(methodStartAddress, methodStartAddress + methodSize, methodIndex));
        return method;
    }

    public void SortMethodAddressRanges()
    {
        CollectionsMarshal.AsSpan(_methodAddressRanges).SortByRef(new UAddressRangeComparer());
    }

    public bool TryFindMethodById(long methodID, [NotNullWhen(true)] out UTraceManagedMethod? method)
    {
        if (_mapManagedMethodIDToMethodIndex.TryGetValue(methodID, out var methodIndex))
        {
            method = _methods[methodIndex];
            return true;
        }
        method = null;
        return false;
    }

    public bool TryFindMethodByAddress(UAddress address, [NotNullWhen(true)] out UTraceManagedMethod? method)
    {
        var ranges = CollectionsMarshal.AsSpan(_methodAddressRanges);
        var comparer = new UAddressRangeFinder(address);
        var index = ranges.BinarySearch(comparer);
        if (index >= 0)
        {
            method = _methods[ranges[index].Index];
            return true;
        }
        method = null;
        return false;
    }
}

public record UTraceMethod(string FullName, UAddress MethodStartAddress, USize MethodSize);

public record UTraceManagedMethod(int ThreadID, long ModuleID, long MethodID, string MethodNamespace, string MethodName, string MethodSignature, int MethodToken, MethodFlags MethodFlags, UAddress MethodStartAddress, USize MethodSize) : UTraceMethod(CreateFullName(MethodNamespace, MethodName), MethodStartAddress, MethodSize)
{
    public UNativeILOffset[]? ILToNativeILOffsets { get; set; }

    private static string CreateFullName(string methodNamespace, string methodName) => string.IsNullOrEmpty(methodNamespace) ? methodName : $"{methodNamespace}.{methodName}";
}


public class UTraceModuleList
{
    private readonly Dictionary<string, UTraceModuleFileIndex> _mapModulePathToIndex = new();
    private readonly List<UTraceModuleFile> _moduleFiles = new();
    private readonly Dictionary<long, int> _mapModuleIDToManagedModule = new();
    private readonly Dictionary<ulong, int> _mapModuleAddressToLoadedModule = new();
    private readonly List<UTraceLoadedModule> _loadedModules = new();
    private readonly List<UAddressRange> _loadedModuleAddressRanges = new();

    public UTraceModuleFile GetOrCreateModuleFile(string filePath)
    {
        if (_mapModulePathToIndex.TryGetValue(filePath, out var index))
        {
            return _moduleFiles[index];
        }

        var moduleFile = new UTraceModuleFile(filePath);
        index = new UTraceModuleFileIndex(_moduleFiles.Count);
        moduleFile.Index = index;
        _mapModulePathToIndex.Add(filePath, index);
        _moduleFiles.Add(moduleFile);
        return moduleFile;
    }

    public ReadOnlySpan<UTraceModuleFile> ModuleFiles => CollectionsMarshal.AsSpan(_moduleFiles);

    public ReadOnlySpan<UTraceLoadedModule> LoadedModules => CollectionsMarshal.AsSpan(_loadedModules);
    
    public bool TryGetManagedModule(long moduleID, [NotNullWhen(true)] out UTraceManagedModule? managedModule)
    {
        if (_mapModuleIDToManagedModule.TryGetValue(moduleID, out var managedModuleIndex))
        {
            managedModule = (UTraceManagedModule)_loadedModules[managedModuleIndex];
            return true;
        }

        managedModule = null;
        return false;
    }

    public UTraceLoadedModule GetOrCreateLoadedModule(string filePath, UAddress baseAddress, USize codeSize)
    {
        if (_mapModuleAddressToLoadedModule.TryGetValue(baseAddress, out var loadedModuleIndex))
        {
            return _loadedModules[loadedModuleIndex];
        }
        
        var moduleFile = GetOrCreateModuleFile(filePath);
        var loadedModule = new UTraceLoadedModule(moduleFile, baseAddress, codeSize);
        loadedModuleIndex = _loadedModules.Count;
        _mapModuleAddressToLoadedModule.Add(baseAddress, loadedModuleIndex);
        _loadedModules.Add(loadedModule);

        _loadedModuleAddressRanges.Add(new(baseAddress, baseAddress + codeSize, loadedModuleIndex));
        CollectionsMarshal.AsSpan(_loadedModuleAddressRanges).SortByRef(new UAddressRangeComparer());

        return loadedModule;
    }

    public bool TryFindModuleByAddress(UAddress address, [NotNullWhen(true)] out UTraceLoadedModule? module)
    {
        var ranges = CollectionsMarshal.AsSpan(_loadedModuleAddressRanges);
        var comparer = new UAddressRangeFinder(address);
        var index = ranges.BinarySearch(comparer);
        if (index >= 0)
        {
            module = _loadedModules[ranges[index].Index];
            return true;
        }
        module = null;
        return false;
    }
    
    public UTraceManagedModule GetOrCreateManagedModule(long moduleID, long assemblyId, string filePath, UAddress baseAddress, USize codeSize)
    {
        if (_mapModuleIDToManagedModule.TryGetValue(moduleID, out var managedModuleIndex))
        {
            return (UTraceManagedModule)_loadedModules[managedModuleIndex];
        }
        var moduleFile = GetOrCreateModuleFile(filePath);
        var managedModule = new UTraceManagedModule(moduleID, assemblyId, moduleFile, baseAddress, codeSize);
        _mapModuleIDToManagedModule.Add(moduleID, _loadedModules.Count);
        _loadedModules.Add(managedModule);
        return managedModule;
    }
}

public record UTraceModuleFile(string FilePath)
{
    public UTraceModuleFileIndex Index { get; internal set; } = UTraceModuleFileIndex.Invalid;

    public Guid SymbolUuid { get; set; }

    public string? SymbolFilePath { get; set; }
}

public record struct UTraceModuleFileIndex(int Value)
{
    public static UTraceModuleFileIndex Invalid = new(-1);

    public bool IsValid => Value >= 0;

    public static implicit operator int(UTraceModuleFileIndex index) => index.Value;

    public static implicit operator UTraceModuleFileIndex(int value) => new(value);
}

public record UTraceLoadedModule(UTraceModuleFile ModuleFile, UAddress BaseAddress, USize CodeSize);

public record UTraceManagedModule(long ModuleID, long AssemblyId, UTraceModuleFile ModuleFile, UAddress BaseAddress, USize CodeSize) : UTraceLoadedModule(ModuleFile, BaseAddress, CodeSize)
{
    public UTraceLoadedModule? NativeModule { get; set; }
}

public record struct UNativeILOffset(int ILOffset, int NativeOffset);


public record struct UAddress(ulong Value)
{
    public override string ToString() => $"0x{Value:X}";

    public static UAddress operator +(UAddress address, ulong offset) => new(address.Value + offset);

    public static UAddress operator -(UAddress address, ulong offset) => new(address.Value - offset);

    public static UAddress operator +(UAddress address, long offset) => new(address.Value + (ulong)offset);

    public static UAddress operator -(UAddress address, long offset) => new(address.Value - (ulong)offset);

    public static UAddress operator +(UAddress address, int offset) => new(address.Value + (ulong)offset);

    public static UAddress operator -(UAddress address, int offset) => new(address.Value - (ulong)offset);

    public static USize operator -(UAddress left, UAddress right) => new(left.Value - right.Value);

    public static implicit operator ulong(UAddress address) => address.Value;

    public static implicit operator UAddress(ulong value) => new(value);
}

public record struct USize(ulong Value)
{
    public override string ToString() => $"0x{Value:X}";

    public static implicit operator ulong(USize size) => size.Value;

    public static implicit operator USize(ulong value) => new(value);

    public static USize operator +(USize size, long offset) => new(size.Value + (ulong)offset);

    public static USize operator -(USize size, long offset) => new(size.Value - (ulong)offset);

    public static USize operator +(USize size, ulong offset) => new(size.Value + offset);

    public static USize operator -(USize size, ulong offset) => new(size.Value - offset);
}

public readonly record struct UAddressRange(UAddress BeginAddress, UAddress EndAddress, int Index)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(ulong address) => address >= BeginAddress && address < EndAddress;
}

readonly record struct UAddressRangeFinder(ulong Address) : IComparable<UAddressRange>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(UAddressRange other)
    {
        return other.Contains(Address) ? 0 : Address.CompareTo(other.BeginAddress);
    }
}

readonly record struct UAddressRangeComparer : IComparerByRef<UAddressRange>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessThan(in UAddressRange left, in UAddressRange right) => left.BeginAddress < right.BeginAddress;
}


readonly struct UNativeOffsetComparer : IComparerByRef<UNativeILOffset>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessThan(in UNativeILOffset left, in UNativeILOffset right) => left.NativeOffset < right.NativeOffset;
}