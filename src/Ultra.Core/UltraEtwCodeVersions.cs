// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Ultra.Core;

/// <summary>
/// The code versions of the methods of a process: each JIT'd (or ReadyToRun) code block with its start, size and optimization tier,
/// and the time it was there, to find the version a code address was in when it was sampled.
/// </summary>
/// <remarks>
/// A version is built from the events that name it (<see cref="UltraEtwFastFilter.CodeVersion"/>): its load, its unload, the CLR
/// rundowns. The same version can be named more than once (at its JIT, then by the rundown at the end): the events of one method
/// at one address are one version, the one with a known tier. The memory of a method that is unloaded (a collectible assembly, a
/// dynamic method) is used again: another method at the same address is another version, each with its lifetime. A version named
/// only by the rundown at the end was there for the whole trace, or since the version before it at its address ended.
/// </remarks>
internal sealed class UltraEtwCodeVersions
{
    // one entry per version, sorted by start, the versions of one start in the order of their lifetimes
    private readonly ulong[] _starts;
    private readonly int[] _sizes;
    private readonly OptimizationTier[] _tiers;
    private readonly double[] _from;    // the version was there from this time (ms, as the events' TimeStampRelativeMSec)...
    private readonly double[] _to;      // ...to before this one
    private readonly ulong[] _reach;    // the furthest end of the versions up to this entry: no entry before it holds an address at or past it

    public static readonly UltraEtwCodeVersions Empty = new([]);

    public UltraEtwCodeVersions(IEnumerable<UltraEtwFastFilter.CodeVersion> events)
    {
        var sorted = events.ToList();
        sorted.Sort(static (a, b) =>
        {
            var order = a.Start.CompareTo(b.Start);
            if (order == 0) order = a.TimeMs.CompareTo(b.TimeMs);
            if (order == 0) order = OrderAtOneTime(a.Event).CompareTo(OrderAtOneTime(b.Event));
            if (order == 0) order = a.MethodId.CompareTo(b.MethodId);
            return order != 0 ? order : a.Size.CompareTo(b.Size);
        });

        var starts = new List<ulong>(sorted.Count);
        var sizes = new List<int>(sorted.Count);
        var tiers = new List<OptimizationTier>(sorted.Count);
        var methods = new List<long>(sorted.Count);
        var from = new List<double>(sorted.Count);
        var to = new List<double>(sorted.Count);

        var open = -1;                                  // the version at the current start that has not ended, if any
        var lastEnd = double.NegativeInfinity;          // when the version before it at this start ended
        for (var i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            if (i == 0 || e.Start != sorted[i - 1].Start)
            {
                open = -1;
                lastEnd = double.NegativeInfinity;
            }

            var same = open >= 0 && methods[open] == e.MethodId && sizes[open] == e.Size;
            if (same && tiers[open] == OptimizationTier.Unknown) tiers[open] = e.Tier;

            switch (e.Event)
            {
                case UltraEtwFastFilter.CodeVersionEvent.Unload:
                    // the version that ends here: the open one, else one that was there before the trace saw it
                    if (open < 0) Add(e, lastEnd, e.TimeMs);
                    else to[open] = e.TimeMs;
                    open = -1;
                    lastEnd = e.TimeMs;
                    break;

                default:
                    if (same) break;                    // named again: at its JIT, by a rundown
                    if (open >= 0)
                    {
                        // another method at the address of one that was not seen to unload: that one ended, at the latest now
                        to[open] = e.TimeMs;
                        lastEnd = e.TimeMs;
                    }

                    // a load starts the version; a rundown names one that was already there
                    open = Add(e, e.Event == UltraEtwFastFilter.CodeVersionEvent.Load ? e.TimeMs : lastEnd, double.PositiveInfinity);
                    break;
            }
        }

        _starts = starts.ToArray();
        _sizes = sizes.ToArray();
        _tiers = tiers.ToArray();
        _from = from.ToArray();
        _to = to.ToArray();
        _reach = new ulong[_starts.Length];
        ulong reach = 0;
        for (var i = 0; i < _starts.Length; i++)
        {
            reach = Math.Max(reach, _starts[i] + (ulong)_sizes[i]);
            _reach[i] = reach;
        }

        int Add(UltraEtwFastFilter.CodeVersion e, double fromTime, double toTime)
        {
            starts.Add(e.Start);
            sizes.Add(e.Size);
            tiers.Add(e.Tier);
            methods.Add(e.MethodId);
            from.Add(fromTime);
            to.Add(toTime);
            return starts.Count - 1;
        }
    }

    /// <summary>The versions: one per method at an address (not one per event).</summary>
    public int Count => _starts.Length;

    /// <summary>
    /// The version whose code held <paramref name="address"/> at <paramref name="timeMs"/> (the time of the sample, as the events'
    /// TimeStampRelativeMSec): its start and tier. When no version with the address in its code was there at that time, the one
    /// nearest in time: the name of the frame comes from TraceLog, which names an address by the method it knows there, whenever
    /// it was sampled. False when the address is in no version's code.
    /// </summary>
    public bool TryFind(ulong address, double timeMs, out ulong start, out OptimizationTier tier)
    {
        // from the last version starting at or before the address, back while one before it can still hold the address
        var index = Array.BinarySearch(_starts, address);
        if (index < 0)
        {
            index = ~index - 1;
        }
        else
        {
            while (index + 1 < _starts.Length && _starts[index + 1] == address) index++;   // the versions of one start: from the last
        }

        var nearest = -1;
        var nearestDistance = double.PositiveInfinity;
        for (var i = index; i >= 0 && _reach[i] > address; i--)
        {
            if (address - _starts[i] >= (ulong)_sizes[i]) continue;
            if (timeMs >= _from[i] && timeMs < _to[i])
            {
                nearest = i;
                break;
            }

            var distance = timeMs < _from[i] ? _from[i] - timeMs : timeMs - _to[i];
            if (distance < nearestDistance)
            {
                nearest = i;
                nearestDistance = distance;
            }
        }

        if (nearest >= 0)
        {
            start = _starts[nearest];
            tier = _tiers[nearest];
            return true;
        }

        start = 0;
        tier = OptimizationTier.Unknown;
        return false;
    }

    // Events of one address at one time: what ends before what starts, a rundown at the end after the load it names again
    private static int OrderAtOneTime(UltraEtwFastFilter.CodeVersionEvent kind) => kind switch
    {
        UltraEtwFastFilter.CodeVersionEvent.Unload => 0,
        UltraEtwFastFilter.CodeVersionEvent.RundownStart => 1,
        UltraEtwFastFilter.CodeVersionEvent.Load => 2,
        _ => 3,
    };
}
