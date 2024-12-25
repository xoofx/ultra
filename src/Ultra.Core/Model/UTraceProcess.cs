// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XenoAtom.Collections;

namespace Ultra.Core.Model;

/// <summary>
/// Represents a process being traced.
/// </summary>
public sealed class UTraceProcess
{
    /// <summary>
    /// Gets or sets the process ID.
    /// </summary>
    public int ProcessID { get; set; }

    /// <summary>
    /// Gets or sets the start time of the process.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Gets or sets the end time of the process.
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Gets or sets the file path of the process.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the command line used to start the process.
    /// </summary>
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the architecture of the process.
    /// </summary>
    public Architecture ProcessArchitecture { get; set; }

    /// <summary>
    /// Gets or sets the runtime identifier of the process.
    /// </summary>
    public string RuntimeIdentifier { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the operating system description of the process.
    /// </summary>
    public string OSDescription { get; set; } = string.Empty;

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

    /// <summary>
    /// Checks if the given time is within the time range of the process.
    /// </summary>
    /// <param name="time">The time to check.</param>
    /// <returns>True if the time is within the time range of the process, false otherwise.</returns>
    public bool WithinTimeRange(DateTime time)
    {
        return  EndTime.Ticks > 0 ? time >= StartTime && time <= EndTime : time >= StartTime;
    }
}