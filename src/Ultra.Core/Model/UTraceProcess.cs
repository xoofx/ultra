// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

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
