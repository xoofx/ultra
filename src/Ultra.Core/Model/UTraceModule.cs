// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a loaded module in the traced process.
/// </summary>
public abstract record UTraceModule(UTraceModuleFile ModuleFile)
{
    /// <summary>
    /// Gets or sets the load time of the module. Time is relative to the start of the session.
    /// </summary>
    public UTimeSpan LoadTime { get; set; }

    /// <summary>
    /// Gets or sets the unload time of the module. Time is relative to the start of the session.
    /// </summary>
    public UTimeSpan UnloadTime { get; set; }
}
