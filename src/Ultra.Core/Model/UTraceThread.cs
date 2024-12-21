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

/// <summary>
/// Represents a process being traced.
/// </summary>
public class UTraceProcess
{
    /// <summary>
    /// Gets or sets the process ID.
    /// </summary>
    public ulong ProcessID { get; set; }

    /// <summary>
    /// Gets or sets the file path of the process.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the command line used to start the process.
    /// </summary>
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>
    /// Gets the list of threads in the traced process.
    /// </summary>
    public UTraceThreadList Threads { get; } = new();

    /// <summary>
    /// Gets the list of loaded modules in the traced process.
    /// </summary>
    public UTraceModuleList Modules { get; } = new();

    /// <summary>
    /// Gets the list of managed methods in the traced process.
    /// </summary>
    public UTraceManagedMethodList ManagedMethods { get; } = new();

    /// <summary>
    /// Gets the list of code addresses in the traced process.
    /// </summary>
    public UCodeAddressList CodeAddresses { get; } = new();

    /// <summary>
    /// Gets the list of call stacks in the traced process.
    /// </summary>
    public UCallStackList CallStacks { get; } = new();
}

/// <summary>
/// Represents a thread in a traced process.
/// </summary>
public record UTraceThread(ulong ThreadID)
{
    private UnsafeList<UProfileSample> _samples = new(1024);

    /// <summary>
    /// Gets or sets the start time of the thread.
    /// </summary>
    public UTimeSpan StartTime { get; set; }

    /// <summary>
    /// Gets or sets the end time of the thread.
    /// </summary>
    public UTimeSpan EndTime { get; set; }

    /// <summary>
    /// Gets or sets the verbose name for the thread.
    /// </summary>
    public string VerboseName { get; set; } = string.Empty;

    /// <summary>
    /// Gets the samples collected for the thread.
    /// </summary>
    public ReadOnlySpan<UProfileSample> Samples => _samples.AsSpan();

    /// <summary>
    /// Gets or sets the CPU time for the thread.
    /// </summary>
    public UTimeSpan CpuTime { get; set; }

    /// <summary>
    /// Clears all collected samples.
    /// </summary>
    public void ClearSamples() => _samples.Clear();

    /// <summary>
    /// Adds a new sample to the thread.
    /// </summary>
    /// <param name="sample">The sample to add.</param>
    public void AddSample(UProfileSample sample) => _samples.Add(sample);
}

/// <summary>
/// Represents a profile sample with call stack index, timestamp, and CPU time.
/// </summary>
public record struct UProfileSample(UCallStackIndex CallStackIndex, UTimeSpan Timestamp, UTimeSpan CpuTime);

/// <summary>
/// A generic list class used to store items of type <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The type of items in the list.</typeparam>
public abstract class UGenericList<T>(int capacity) : IEnumerable<T>
{
    protected UnsafeList<T> List = new(capacity);

    /// <summary>
    /// Initializes a new instance of the <see cref="UGenericList{T}"/> class.
    /// </summary>
    protected UGenericList() : this(0)
    {
    }

    /// <summary>
    /// Gets the items in the list as a span.
    /// </summary>
    public ReadOnlySpan<T> Items => List.AsSpan();

    [EditorBrowsable(EditorBrowsableState.Never)]
    public ReadOnlySpan<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => List.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => List.GetEnumerator();
}

/// <summary>
/// Represents a list of <see cref="UTraceThread"/> instances.
/// </summary>
public class UTraceThreadList : UGenericList<UTraceThread>
{
    private readonly Dictionary<ulong, int> _mapThreadIDToIndex = new();

    /// <summary>
    /// Gets or creates a thread with the specified thread ID.
    /// </summary>
    /// <param name="threadID">The thread ID.</param>
    /// <returns>The <see cref="UTraceThread"/> instance.</returns>
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

    /// <summary>
    /// Orders the threads by CPU time in descending order.
    /// </summary>
    public void OrderByCpuTimeDescending() => List.AsSpan().SortByRef(new ThreadCpuTimeComparer());

    private struct ThreadCpuTimeComparer : IComparerByRef<UTraceThread>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool LessThan(in UTraceThread left, in UTraceThread right) => left.CpuTime < right.CpuTime;
    }
}

/// <summary>
/// Represents a list of <see cref="UCallStackFrame"/> instances.
/// </summary>
public class UCallStackList : UGenericList<UCallStackFrame>
{
    private UnsafeDictionary<UCallStackFrame, UCallStackIndex> _uniqueStacks = new(65536);

    /// <summary>
    /// Initializes a new instance of the <see cref="UCallStackList"/> class.
    /// </summary>
    public UCallStackList() : base(65536)
    {
        // First element UCallStackIndex(0) is reserved for root
        Push(new(UCallStackIndex.Invalid, UCodeAddressIndex.Invalid));
    }

    /// <summary>
    /// Gets the call stack frame at the specified index.
    /// </summary>
    /// <param name="index">The call stack index.</param>
    /// <returns>The <see cref="UCallStackFrame"/> instance.</returns>
    public UCallStackFrame this[UCallStackIndex index] => List[index];

    /// <summary>
    /// Inserts a call stack into the list and returns the root index.
    /// </summary>
    /// <param name="callstack">A span of code address indices representing the call stack.</param>
    /// <returns>The root index of the call stack.</returns>
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

/// <summary>
/// Represents a frame in a call stack with a parent index and a code address index.
/// </summary>
public readonly record struct UCallStackFrame(UCallStackIndex ParentCallStackIndex, UCodeAddressIndex CodeAddressIndex);

/// <summary>
/// Represents a list of <see cref="UCodeAddress"/> instances.
/// </summary>
public class UCodeAddressList : UGenericList<UAddress>
{
    private readonly Dictionary<UAddress, UCodeAddressIndex> _mapAddressToIndex = new();

    /// <summary>
    /// Gets the code address at the specified index.
    /// </summary>
    /// <param name="index">The code address index.</param>
    /// <returns>The <see cref="UAddress"/> instance.</returns>
    public UAddress this[UCodeAddressIndex index] => List[index];

    /// <summary>
    /// Gets or creates a code address for the specified address.
    /// </summary>
    /// <param name="address">The code address.</param>
    /// <returns>The <see cref="UCodeAddressIndex"/> for the address.</returns>
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

/// <summary>
/// Represents a code address index.
/// </summary>
public record struct UCodeAddressIndex(int Value)
{
    /// <summary>
    /// Gets the invalid code address index.
    /// </summary>
    public static UCodeAddressIndex Invalid => new(-1);

    /// <summary>
    /// Gets a value indicating whether the index is valid.
    /// </summary>
    public bool IsValid => Value >= 0;

    /// <summary>
    /// Implicit conversion to <see cref="int"/>.
    /// </summary>
    public static implicit operator int(UCodeAddressIndex index) => index.Value;

    /// <summary>
    /// Explicit conversion from <see cref="int"/>.
    /// </summary>
    public static explicit operator UCodeAddressIndex(int value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Represents a call stack index.
/// </summary>
public record struct UCallStackIndex(int Value)
{
    /// <summary>
    /// Gets the invalid call stack index.
    /// </summary>
    public static UCallStackIndex Invalid => new(-1);

    /// <summary>
    /// Gets a value indicating whether the index is valid.
    /// </summary>
    public bool IsValid => Value >= 0;

    /// <summary>
    /// Implicit conversion to <see cref="int"/>.
    /// </summary>
    public static implicit operator int(UCallStackIndex index) => index.Value;

    /// <summary>
    /// Explicit conversion from <see cref="int"/>.
    /// </summary>
    public static explicit operator UCallStackIndex(int value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Represents a list of <see cref="UTraceManagedMethod"/> instances.
/// </summary>
public class UTraceManagedMethodList : UGenericList<UTraceManagedMethod>
{
    private UnsafeDictionary<UAddress, int> _mapMethodAddressToMethodIndex = new();
    private UnsafeDictionary<long, int> _mapManagedMethodIDToMethodIndex = new();
    private UnsafeList<UAddressRange> _methodAddressRanges = new();

    /// <summary>
    /// Gets or creates a managed method based on the provided details.
    /// </summary>
    /// <param name="threadID">The thread ID.</param>
    /// <param name="moduleID">The module ID.</param>
    /// <param name="methodID">The method ID.</param>
    /// <param name="methodNamespace">The namespace of the method.</param>
    /// <param name="methodName">The name of the method.</param>
    /// <param name="methodSignature">The signature of the method.</param>
    /// <param name="methodToken">The method token.</param>
    /// <param name="methodFlags">The method flags.</param>
    /// <param name="methodStartAddress">The starting address of the method.</param>
    /// <param name="methodSize">The size of the method.</param>
    /// <returns>The <see cref="UTraceManagedMethod"/> instance.</returns>
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

    /// <summary>
    /// Sorts the method address ranges.
    /// </summary>
    public void SortMethodAddressRanges()
    {
        _methodAddressRanges.AsSpan().SortByRef(new UAddressRangeComparer());
    }

    /// <summary>
    /// Tries to find a method by its ID.
    /// </summary>
    /// <param name="methodID">The method ID.</param>
    /// <param name="method">The method found, if any.</param>
    /// <returns>True if the method was found, otherwise false.</returns>
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

    /// <summary>
    /// Tries to find a method by its address.
    /// </summary>
    /// <param name="address">The address of the method.</param>
    /// <param name="method">The method found, if any.</param>
    /// <returns>True if the method was found, otherwise false.</returns>
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

/// <summary>
/// Represents a method in a traced process, including the method's full name, start address, and size.
/// </summary>
public record UTraceMethod(string FullName, UAddress MethodStartAddress, USize MethodSize);

/// <summary>
/// Represents a managed method in a traced process.
/// </summary>
public record UTraceManagedMethod(int ThreadID, long ModuleID, long MethodID, string MethodNamespace, string MethodName, string MethodSignature, int MethodToken, MethodFlags MethodFlags, UAddress MethodStartAddress, USize MethodSize) : UTraceMethod(CreateFullName(MethodNamespace, MethodName), MethodStartAddress, MethodSize)
{
    /// <summary>
    /// Gets or sets the native IL offsets for the method.
    /// </summary>
    public UNativeILOffset[]? ILToNativeILOffsets { get; set; }

    private static string CreateFullName(string methodNamespace, string methodName) => string.IsNullOrEmpty(methodNamespace) ? methodName : $"{methodNamespace}.{methodName}";
}

/// <summary>
/// Represents a list of <see cref="UTraceLoadedModule"/> instances.
/// </summary>
public class UTraceModuleList : UGenericList<UTraceLoadedModule>
{
    private UnsafeDictionary<string, UTraceModuleFileIndex> _mapModulePathToIndex = new();
    private UnsafeList<UTraceModuleFile> _moduleFiles = new();
    private UnsafeDictionary<long, int> _mapModuleIDToManagedModule = new();
    private UnsafeDictionary<ulong, int> _mapModuleAddressToLoadedModule = new();
    private UnsafeList<UAddressRange> _loadedModuleAddressRanges = new();

    /// <summary>
    /// Gets or creates a module file based on the file path.
    /// </summary>
    /// <param name="filePath">The file path of the module.</param>
    /// <returns>The <see cref="UTraceModuleFile"/> instance.</returns>
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

    /// <summary>
    /// Gets the module files in the list.
    /// </summary>
    public ReadOnlySpan<UTraceModuleFile> ModuleFiles => _moduleFiles.AsSpan();

    /// <summary>
    /// Tries to find a managed module by its module ID.
    /// </summary>
    /// <param name="moduleID">The module ID.</param>
    /// <param name="managedModule">The managed module found, if any.</param>
    /// <returns>True if the managed module was found, otherwise false.</returns>
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

    /// <summary>
    /// Gets or creates a loaded module based on the file path, base address, and code size.
    /// </summary>
    /// <param name="filePath">The file path of the module.</param>
    /// <param name="baseAddress">The base address of the module.</param>
    /// <param name="codeSize">The size of the code.</param>
    /// <returns>The <see cref="UTraceLoadedModule"/> instance.</returns>
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

    /// <summary>
    /// Tries to find a module by its address.
    /// </summary>
    /// <param name="address">The address of the module.</param>
    /// <param name="module">The module found, if any.</param>
    /// <returns>True if the module was found, otherwise false.</returns>
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

    /// <summary>
    /// Gets or creates a managed module based on the module ID, assembly ID, file path, base address, and code size.
    /// </summary>
    /// <param name="moduleID">The module ID.</param>
    /// <param name="assemblyId">The assembly ID.</param>
    /// <param name="filePath">The file path of the module.</param>
    /// <param name="baseAddress">The base address of the module.</param>
    /// <param name="codeSize">The size of the module.</param>
    /// <returns>The <see cref="UTraceManagedModule"/> instance.</returns>
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

/// <summary>
/// Represents a module file that is loaded in the traced process.
/// </summary>
public record UTraceModuleFile(string FilePath)
{
    /// <summary>
    /// Gets or sets the module file index.
    /// </summary>
    public UTraceModuleFileIndex Index { get; internal set; } = UTraceModuleFileIndex.Invalid;

    /// <summary>
    /// Gets or sets the symbol UUID for the module file.
    /// </summary>
    public Guid SymbolUuid { get; set; }

    /// <summary>
    /// Gets or sets the symbol file path for the module.
    /// </summary>
    public string? SymbolFilePath { get; set; }

    /// <summary>
    /// Gets or sets the load time of the module.
    /// </summary>
    public UTimeSpan LoadTime { get; set; }
}

/// <summary>
/// Represents a module file index.
/// </summary>
public record struct UTraceModuleFileIndex(int Value)
{
    /// <summary>
    /// Gets the invalid module file index.
    /// </summary>
    public static UTraceModuleFileIndex Invalid => new(-1);

    /// <summary>
    /// Gets a value indicating whether the index is valid.
    /// </summary>
    public bool IsValid => Value >= 0;

    /// <summary>
    /// Implicit conversion to <see cref="int"/>.
    /// </summary>
    public static implicit operator int(UTraceModuleFileIndex index) => index.Value;

    /// <summary>
    /// Explicit conversion from <see cref="int"/>.
    /// </summary>
    public static explicit operator UTraceModuleFileIndex(int value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Represents a loaded module in the traced process.
/// </summary>
public record UTraceLoadedModule(UTraceModuleFile ModuleFile, UAddress BaseAddress, USize CodeSize);

/// <summary>
/// Represents a managed module in the traced process.
/// </summary>
public record UTraceManagedModule(long ModuleID, long AssemblyId, UTraceModuleFile ModuleFile, UAddress BaseAddress, USize CodeSize) : UTraceLoadedModule(ModuleFile, BaseAddress, CodeSize)
{
    /// <summary>
    /// Gets or sets the native module if available.
    /// </summary>
    public UTraceLoadedModule? NativeModule { get; set; }
}

/// <summary>
/// Represents an IL-to-native offset.
/// </summary>
public record struct UNativeILOffset(int ILOffset, int NativeOffset);

/// <summary>
/// Represents an address in the traced process.
/// </summary>
public record struct UAddress(ulong Value) : IComparable<UAddress>, IComparable
{
    /// <inheritdoc />
    public override string ToString() => $"0x{Value:X}";

    /// <summary>
    /// Adds an offset to the address.
    /// </summary>
    public static UAddress operator +(UAddress address, ulong offset) => new(address.Value + offset);

    /// <summary>
    /// Subtracts an offset from the address.
    /// </summary>
    public static UAddress operator -(UAddress address, ulong offset) => new(address.Value - offset);

    /// <summary>
    /// Adds an offset to the address.
    /// </summary>
    public static UAddress operator +(UAddress address, long offset) => new(address.Value + (ulong)offset);

    /// <summary>
    /// Subtracts an offset from the address.
    /// </summary>
    public static UAddress operator -(UAddress address, long offset) => new(address.Value - (ulong)offset);

    /// <summary>
    /// Adds an offset to the address.
    /// </summary>
    public static UAddress operator +(UAddress address, int offset) => new(address.Value + (ulong)offset);

    /// <summary>
    /// Subtracts an offset from the address.
    /// </summary>
    public static UAddress operator -(UAddress address, int offset) => new(address.Value - (ulong)offset);

    /// <summary>
    /// Subtracts two addresses to return the size difference.
    /// </summary>
    public static USize operator -(UAddress left, UAddress right) => new(left.Value - right.Value);

    /// <summary>
    /// Implicit conversion to <see cref="ulong"/>.
    /// </summary>
    public static implicit operator ulong(UAddress address) => address.Value;

    /// <summary>
    /// Explicit conversion from <see cref="ulong"/>.
    /// </summary>
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

    /// <summary>
    /// Compares two addresses for less than.
    /// </summary>
    public static bool operator <(UAddress left, UAddress right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Compares two addresses for greater than.
    /// </summary>
    public static bool operator >(UAddress left, UAddress right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Compares two addresses for less than or equal.
    /// </summary>
    public static bool operator <=(UAddress left, UAddress right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Compares two addresses for greater than or equal.
    /// </summary>
    public static bool operator >=(UAddress left, UAddress right)
    {
        return left.CompareTo(right) >= 0;
    }
}

/// <summary>
/// Represents a size in the traced process.
/// </summary>
public record struct USize(ulong Value) : IComparable<USize>, IComparable
{
    /// <inheritdoc />
    public override string ToString() => $"0x{Value:X}";

    /// <summary>
    /// Implicit conversion to <see cref="ulong"/>.
    /// </summary>
    public static implicit operator ulong(USize size) => size.Value;

    /// <summary>
    /// Implicit conversion from <see cref="ulong"/>.
    /// </summary>
    public static implicit operator USize(ulong value) => new(value);

    /// <summary>
    /// Adds an offset to the size.
    /// </summary>
    public static USize operator +(USize size, long offset) => new(size.Value + (ulong)offset);

    /// <summary>
    /// Subtracts an offset from the size.
    /// </summary>
    public static USize operator -(USize size, long offset) => new(size.Value - (ulong)offset);

    /// <summary>
    /// Adds an offset to the size.
    /// </summary>
    public static USize operator +(USize size, ulong offset) => new(size.Value + offset);

    /// <summary>
    /// Subtracts an offset from the size.
    /// </summary>
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

    /// <summary>
    /// Compares two sizes for less than.
    /// </summary>
    public static bool operator <(USize left, USize right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Compares two sizes for greater than.
    /// </summary>
    public static bool operator >(USize left, USize right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Compares two sizes for less than or equal.
    /// </summary>
    public static bool operator <=(USize left, USize right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Compares two sizes for greater than or equal.
    /// </summary>
    public static bool operator >=(USize left, USize right)
    {
        return left.CompareTo(right) >= 0;
    }
}

/// <summary>
/// Represents a time span in the traced process.
/// </summary>
public readonly record struct UTimeSpan(TimeSpan Value) : IComparable<UTimeSpan>, IComparable
{
    /// <summary>
    /// Gets the time span value in milliseconds.
    /// </summary>
    public double InMs => Value.TotalMilliseconds;

    /// <inheritdoc />
    public override string ToString() => $"{InMs:#,##0.0###}ms";

    /// <summary>
    /// Implicit conversion to <see cref="TimeSpan"/>.
    /// </summary>
    public static implicit operator TimeSpan(UTimeSpan timeSpan) => timeSpan.Value;

    /// <summary>
    /// Implicit conversion from <see cref="TimeSpan"/>.
    /// </summary>
    public static implicit operator UTimeSpan(TimeSpan value) => new(value);

    /// <summary>
    /// Creates a <see cref="UTimeSpan"/> from milliseconds.
    /// </summary>
    public static UTimeSpan FromMilliseconds(double timeInMs) => TimeSpan.FromMilliseconds(timeInMs);

    /// <inheritdoc />
    public int CompareTo(UTimeSpan other)
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

        return obj is UTimeSpan other ? CompareTo(other) : throw new ArgumentException($"Object must be of type {nameof(UTimeSpan)}");
    }

    /// <summary>
    /// Compares two time spans for less than.
    /// </summary>
    public static bool operator <(UTimeSpan left, UTimeSpan right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Compares two time spans for greater than.
    /// </summary>
    public static bool operator >(UTimeSpan left, UTimeSpan right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Compares two time spans for less than or equal.
    /// </summary>
    public static bool operator <=(UTimeSpan left, UTimeSpan right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Compares two time spans for greater than or equal.
    /// </summary>
    public static bool operator >=(UTimeSpan left, UTimeSpan right)
    {
        return left.CompareTo(right) >= 0;
    }
}

/// <summary>
/// Represents an address range in the traced process.
/// </summary>
public readonly record struct UAddressRange(UAddress BeginAddress, UAddress EndAddress, int Index)
{
    /// <summary>
    /// Determines if the address is contained within the range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(ulong address) => address >= BeginAddress && address < EndAddress;
}

/// <summary>
/// Represents a comparer for address ranges.
/// </summary>
readonly record struct UAddressRangeFinder(ulong Address) : IComparable<UAddressRange>
{
    /// <summary>
    /// Compares the address range finder with another address range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(UAddressRange other)
    {
        return other.Contains(Address) ? 0 : Address.CompareTo(other.BeginAddress);
    }
}

/// <summary>
/// Represents a comparer for address ranges based on their beginning address.
/// </summary>
readonly record struct UAddressRangeComparer : IComparerByRef<UAddressRange>
{
    /// <summary>
    /// Compares two address ranges for less than.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessThan(in UAddressRange left, in UAddressRange right) => left.BeginAddress < right.BeginAddress;
}

/// <summary>
/// Represents a comparer for native IL offsets.
/// </summary>
readonly struct UNativeOffsetComparer : IComparerByRef<UNativeILOffset>
{
    /// <summary>
    /// Compares two native IL offsets for less than.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool LessThan(in UNativeILOffset left, in UNativeILOffset right) => left.NativeOffset < right.NativeOffset;
}
