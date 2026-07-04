// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// Represents a list of <see cref="UCallStackFrame"/> instances.
/// </summary>
public sealed class UCallStackList : UGenericList<UCallStackFrame>
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