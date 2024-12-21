// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Runtime.CompilerServices;

using XenoAtom.Collections;

namespace Ultra.Core.Model;

public class UTraceProcess
{
    public ulong ProcessID { get; set; }

    public string FilePath { get; set; } = string.Empty;

    public string CommandLine { get; set; } = string.Empty;

    public UTraceThreadList Threads { get; } = new();

    public UTraceModuleList Modules { get; } = new();

    public UTraceManagedMethodList ManagedMethods { get; } = new();

    public UCodeAddressList CodeAddresses { get; } = new();

    public UCallStackList CallStacks { get; } = new();

}

public record UTraceThread(ulong ThreadID)
{
    private UnsafeList<UProfileSample> _samples = new(1024);

    public UTimeSpan StartTime { get; set; }

    public UTimeSpan EndTime { get; set; }

    public string VerboseName { get; set; } = string.Empty;

    public ReadOnlySpan<UProfileSample> Samples => _samples.AsSpan();

    public UTimeSpan CpuTime { get; set; }

    public void ClearSamples() => _samples.Clear();

    public void AddSample(UProfileSample sample) => _samples.Add(sample);
}

public record struct UProfileSample(UCallStackIndex CallStackIndex, UTimeSpan Timestamp, UTimeSpan CpuTime);

public abstract class UGenericList<T>(int capacity) : IEnumerable<T>
{
    protected UnsafeList<T> List = new(capacity);

    protected UGenericList() : this(0)
    {
    }

    public ReadOnlySpan<T> Items => List.AsSpan();

    [EditorBrowsable(EditorBrowsableState.Never)]
    public ReadOnlySpan<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => List.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => List.GetEnumerator();
}

public class UTraceThreadList : UGenericList<UTraceThread>
{
    private readonly Dictionary<ulong, int> _mapThreadIDToIndex = new();

    public UTraceThread GetOrCreateThread(ulong threadID)
    {
        if (_mapThreadIDToIndex.TryGetValue(threadID, out var index))
        {
            return List[index];
        }
        var thread = new UTraceThread(threadID);
        index = List.Count;
        _mapThreadIDToIndex.Add(threadID, index);
        List.Add(thread);
        return thread;
    }

    public void OrderByCpuTimeDescending() => List.AsSpan().SortByRef(new ThreadCpuTimeComparer());

    private struct ThreadCpuTimeComparer : IComparerByRef<UTraceThread>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool LessThan(in UTraceThread left, in UTraceThread right) => left.CpuTime < right.CpuTime;
    }
}

public class UCallStackList : UGenericList<UCallStackFrame>
{
    private UnsafeDictionary<UCallStackFrame, UCallStackIndex> _uniqueStacks = new(65536);

    public UCallStackList() : base(65536)
    {
        // First element UCallStackIndex(0) is reserved for root
        Push(new(UCallStackIndex.Invalid, UCodeAddressIndex.Invalid));
    }

    public UCallStackFrame this[UCallStackIndex index] => List[index];

    public UCallStackIndex InsertCallStack(ReadOnlySpan<UCodeAddressIndex> callstack)
    {
        var parentIndex = (UCallStackIndex)0;
        for (var i = callstack.Length - 1; i >= 0; --i)
        {
            var codeAddressIndex = callstack[i];
            var stackInfo = new UCallStackFrame(parentIndex, codeAddressIndex);
            parentIndex = Push(stackInfo);
        }
        return parentIndex;
    }

    private UCallStackIndex Push(UCallStackFrame stackFrame)
    {
        if (_uniqueStacks.TryGetValue(stackFrame, out var index))
        {
            return index;
        }

        index = new UCallStackIndex(List.Count);
        List.Add(stackFrame);
        _uniqueStacks.Add(stackFrame, index);
        return index;
    }
}

public readonly record struct UCallStackFrame(UCallStackIndex ParentCallStackIndex, UCodeAddressIndex CodeAddressIndex);

public class UCodeAddressList : UGenericList<UAddress>
{
    private readonly Dictionary<UAddress, UCodeAddressIndex> _mapAddressToIndex = new();
    
    public UAddress this[UCodeAddressIndex index] => List[index];

    public UCodeAddressIndex GetOrCreateAddress(UAddress address)
    {
        if (_mapAddressToIndex.TryGetValue(address, out var index))
        {
            return index;
        }
        var codeAddressIndex = new UCodeAddressIndex(List.Count);
        _mapAddressToIndex.Add(address, codeAddressIndex);
        List.Add(address);
        return codeAddressIndex;
    }
}

public record struct UCodeAddressIndex(int Value)
{
    public static UCodeAddressIndex Invalid => new(-1);

    public bool IsValid => Value >= 0;

    public static implicit operator int(UCodeAddressIndex index) => index.Value;
    public static explicit operator UCodeAddressIndex(int value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

public record struct UCallStackIndex(int Value)
{
    public static UCallStackIndex Invalid => new(-1);

    public bool IsValid => Value >= 0;
    public static implicit operator int(UCallStackIndex index) => index.Value;
    public static explicit operator UCallStackIndex(int value) => new(value);
    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

public class UTraceManagedMethodList : UGenericList<UTraceManagedMethod>
{
    private UnsafeDictionary<UAddress, int> _mapMethodAddressToMethodIndex = new();
    private UnsafeDictionary<long, int> _mapManagedMethodIDToMethodIndex = new();
    private UnsafeList<UAddressRange> _methodAddressRanges = new();

    public UTraceManagedMethod GetOrCreateManagedMethod(int threadID, long moduleID, long methodID, string methodNamespace, string methodName, string methodSignature, int methodToken, MethodFlags methodFlags, UAddress methodStartAddress, USize methodSize)
    {
        if (_mapManagedMethodIDToMethodIndex.TryGetValue(methodID, out var methodIndex))
        {
            return List[methodIndex];
        }
        var method = new UTraceManagedMethod(threadID, moduleID, methodID, methodNamespace, methodName, methodSignature, methodToken, methodFlags, methodStartAddress, methodSize);
        methodIndex = List.Count;
        _mapManagedMethodIDToMethodIndex.Add(methodID, methodIndex);
        List.Add(method);
        _methodAddressRanges.Add(new(methodStartAddress, methodStartAddress + methodSize, methodIndex));
        return method;
    }

    public void SortMethodAddressRanges()
    {
        _methodAddressRanges.AsSpan().SortByRef(new UAddressRangeComparer());
    }

    public bool TryFindMethodById(long methodID, [NotNullWhen(true)] out UTraceManagedMethod? method)
    {
        if (_mapManagedMethodIDToMethodIndex.TryGetValue(methodID, out var methodIndex))
        {
            method = List[methodIndex];
            return true;
        }
        method = null;
        return false;
    }

    public bool TryFindMethodByAddress(UAddress address, [NotNullWhen(true)] out UTraceManagedMethod? method)
    {
        var ranges = _methodAddressRanges.AsSpan();
        var comparer = new UAddressRangeFinder(address);
        var index = ranges.BinarySearch(comparer);
        if (index >= 0)
        {
            method = List[ranges[index].Index];
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


public class UTraceModuleList : UGenericList<UTraceLoadedModule>
{
    private UnsafeDictionary<string, UTraceModuleFileIndex> _mapModulePathToIndex = new();
    private UnsafeList<UTraceModuleFile> _moduleFiles = new();
    private UnsafeDictionary<long, int> _mapModuleIDToManagedModule = new();
    private UnsafeDictionary<ulong, int> _mapModuleAddressToLoadedModule = new();
    private UnsafeList<UAddressRange> _loadedModuleAddressRanges = new();

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

    public ReadOnlySpan<UTraceModuleFile> ModuleFiles => _moduleFiles.AsSpan();
    
    public bool TryGetManagedModule(long moduleID, [NotNullWhen(true)] out UTraceManagedModule? managedModule)
    {
        if (_mapModuleIDToManagedModule.TryGetValue(moduleID, out var managedModuleIndex))
        {
            managedModule = (UTraceManagedModule)List[managedModuleIndex];
            return true;
        }

        managedModule = null;
        return false;
    }

    public UTraceLoadedModule GetOrCreateLoadedModule(string filePath, UAddress baseAddress, USize codeSize)
    {
        if (_mapModuleAddressToLoadedModule.TryGetValue(baseAddress, out var loadedModuleIndex))
        {
            return List[loadedModuleIndex];
        }
        
        var moduleFile = GetOrCreateModuleFile(filePath);
        var loadedModule = new UTraceLoadedModule(moduleFile, baseAddress, codeSize);
        loadedModuleIndex = List.Count;
        _mapModuleAddressToLoadedModule.Add(baseAddress, loadedModuleIndex);
        List.Add(loadedModule);

        _loadedModuleAddressRanges.Add(new(baseAddress, baseAddress + codeSize, loadedModuleIndex));
        _loadedModuleAddressRanges.AsSpan().SortByRef(new UAddressRangeComparer());

        return loadedModule;
    }

    public bool TryFindModuleByAddress(UAddress address, [NotNullWhen(true)] out UTraceLoadedModule? module)
    {
        var ranges = _loadedModuleAddressRanges.AsSpan();
        var comparer = new UAddressRangeFinder(address);
        var index = ranges.BinarySearch(comparer);
        if (index >= 0)
        {
            module = List[ranges[index].Index];
            return true;
        }
        module = null;
        return false;
    }
    
    public UTraceManagedModule GetOrCreateManagedModule(long moduleID, long assemblyId, string filePath, UAddress baseAddress, USize codeSize)
    {
        if (_mapModuleIDToManagedModule.TryGetValue(moduleID, out var managedModuleIndex))
        {
            return (UTraceManagedModule)List[managedModuleIndex];
        }
        var moduleFile = GetOrCreateModuleFile(filePath);
        var managedModule = new UTraceManagedModule(moduleID, assemblyId, moduleFile, baseAddress, codeSize);
        _mapModuleIDToManagedModule.Add(moduleID, List.Count);
        List.Add(managedModule);
        return managedModule;
    }
}

public record UTraceModuleFile(string FilePath)
{
    public UTraceModuleFileIndex Index { get; internal set; } = UTraceModuleFileIndex.Invalid;

    public Guid SymbolUuid { get; set; }

    public string? SymbolFilePath { get; set; }

    public UTimeSpan LoadTime { get; set; }
}

public record struct UTraceModuleFileIndex(int Value)
{
    public static UTraceModuleFileIndex Invalid => new(-1);

    public bool IsValid => Value >= 0;

    public static implicit operator int(UTraceModuleFileIndex index) => index.Value;

    public static explicit operator UTraceModuleFileIndex(int value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

public record UTraceLoadedModule(UTraceModuleFile ModuleFile, UAddress BaseAddress, USize CodeSize);

public record UTraceManagedModule(long ModuleID, long AssemblyId, UTraceModuleFile ModuleFile, UAddress BaseAddress, USize CodeSize) : UTraceLoadedModule(ModuleFile, BaseAddress, CodeSize)
{
    public UTraceLoadedModule? NativeModule { get; set; }
}

public record struct UNativeILOffset(int ILOffset, int NativeOffset);


public record struct UAddress(ulong Value) : IComparable<UAddress>, IComparable
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

    /// <inheritdoc />
    public int CompareTo(UAddress other)
    {
        return Value.CompareTo(other.Value);
    }

    /// <inheritdoc />
    public int CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        return obj is UAddress other ? CompareTo(other) : throw new ArgumentException($"Object must be of type {nameof(UAddress)}");
    }

    public static bool operator <(UAddress left, UAddress right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(UAddress left, UAddress right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(UAddress left, UAddress right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(UAddress left, UAddress right)
    {
        return left.CompareTo(right) >= 0;
    }
}

public record struct USize(ulong Value) : IComparable<USize>, IComparable
{
    public override string ToString() => $"0x{Value:X}";

    public static implicit operator ulong(USize size) => size.Value;

    public static implicit operator USize(ulong value) => new(value);

    public static USize operator +(USize size, long offset) => new(size.Value + (ulong)offset);

    public static USize operator -(USize size, long offset) => new(size.Value - (ulong)offset);

    public static USize operator +(USize size, ulong offset) => new(size.Value + offset);

    public static USize operator -(USize size, ulong offset) => new(size.Value - offset);

    /// <inheritdoc />
    public int CompareTo(USize other)
    {
        return Value.CompareTo(other.Value);
    }

    /// <inheritdoc />
    public int CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        return obj is USize other ? CompareTo(other) : throw new ArgumentException($"Object must be of type {nameof(USize)}");
    }

    public static bool operator <(USize left, USize right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(USize left, USize right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(USize left, USize right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(USize left, USize right)
    {
        return left.CompareTo(right) >= 0;
    }
}

public readonly record struct UTimeSpan(TimeSpan Value) : IComparable<UTimeSpan>, IComparable
{
    public double InMs => Value.TotalMilliseconds;

    public override string ToString() => $"{InMs:#,##0.0###}ms";
    public static implicit operator TimeSpan(UTimeSpan timeSpan) => timeSpan.Value;

    public static implicit operator UTimeSpan(TimeSpan value) => new(value);

    public static UTimeSpan FromMilliseconds(double timeInMs) => TimeSpan.FromMilliseconds(timeInMs);

    public int CompareTo(UTimeSpan other)
    {
        return Value.CompareTo(other.Value);
    }

    public int CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        return obj is UTimeSpan other ? CompareTo(other) : throw new ArgumentException($"Object must be of type {nameof(UTimeSpan)}");
    }

    public static bool operator <(UTimeSpan left, UTimeSpan right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(UTimeSpan left, UTimeSpan right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(UTimeSpan left, UTimeSpan right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(UTimeSpan left, UTimeSpan right)
    {
        return left.CompareTo(right) >= 0;
    }
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