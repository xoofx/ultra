// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// Represents a thread in a traced process.
/// </summary>
public record UTraceThread(ulong ThreadID)
{
    private UnsafeList<UTraceSample> _samples = new(1024);

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
    public ReadOnlySpan<UTraceSample> Samples => _samples.AsSpan();

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
    public void AddSample(UTraceSample sample) => _samples.Add(sample);
}