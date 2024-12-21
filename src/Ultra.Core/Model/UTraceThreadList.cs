// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;
using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// Represents a list of <see cref="UTraceThread"/> instances.
/// </summary>
public sealed class UTraceThreadList : UGenericList<UTraceThread>
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