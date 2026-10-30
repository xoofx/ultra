// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// A sample of a hardware counter (e.g. cache misses) taken on a thread.
/// </summary>
/// <param name="CallStackIndex">The call stack at the time of the sample.</param>
/// <param name="Timestamp">The time of the sample.</param>
/// <param name="Count">The number of counter events the sample stands for (the sampling period at that time).</param>
public record struct UTraceCounterSample(UCallStackIndex CallStackIndex, UTimeSpan Timestamp, ulong Count);

/// <summary>
/// The samples of one hardware counter on a thread.
/// </summary>
public sealed class UTraceCounterSampleList(string name) : UGenericList<UTraceCounterSample>(1024)
{
    /// <summary>
    /// Gets the name of the counter (e.g. "cycles", "cache-misses").
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets the sum of the counts of all the samples.
    /// </summary>
    public ulong TotalCount { get; private set; }

    /// <summary>
    /// Adds a sample and accounts its count in <see cref="TotalCount"/>.
    /// </summary>
    public void Add(UTraceCounterSample sample)
    {
        List.Add(sample);
        TotalCount += sample.Count;
    }

    /// <summary>Puts the samples in time order (see <see cref="UTraceSampleList.SortByTimestamp"/>).</summary>
    public void SortByTimestamp() => List.AsSpan().SortByRef(new TimestampComparer());

    private readonly struct TimestampComparer : IComparerByRef<UTraceCounterSample>
    {
        public bool LessThan(in UTraceCounterSample left, in UTraceCounterSample right) => left.Timestamp < right.Timestamp;
    }
}
