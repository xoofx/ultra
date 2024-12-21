// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

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