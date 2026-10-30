// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// Represents a thread in a traced process.
/// </summary>
public sealed record UTraceThread(ulong ThreadID)
{
    /// <summary>
    /// Gets or sets the start time of the thread.
    /// </summary>
    public UTimeSpan StartTime { get; set; }

    /// <summary>
    /// Gets or sets the end time of the thread.
    /// </summary>
    public UTimeSpan StopTime { get; set; }

    /// <summary>
    /// Gets or sets the verbose name for the thread.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets the samples collected for the thread.
    /// </summary>
    public UTraceSampleList Samples { get; } = new();

    /// <summary>
    /// Gets the markers collected for the thread.
    /// </summary>
    public UTraceMarkerList Markers { get; } = new();

    /// <summary>
    /// Gets or sets the CPU time for the thread.
    /// </summary>
    public UTimeSpan CpuTime { get; set; }

    /// <summary>
    /// Gets the samples of the hardware counters recorded on this thread (Linux, one list per counter).
    /// </summary>
    public List<UTraceCounterSampleList> CounterSamples { get; } = new();

    /// <summary>
    /// Gets or creates the sample list of a counter.
    /// </summary>
    public UTraceCounterSampleList GetOrCreateCounterSamples(string name)
    {
        foreach (var counter in CounterSamples)
        {
            if (counter.Name == name) return counter;
        }
        var list = new UTraceCounterSampleList(name);
        CounterSamples.Add(list);
        return list;
    }
}