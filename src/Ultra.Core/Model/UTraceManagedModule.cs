// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a managed module in the traced process.
/// </summary>
public sealed record UTraceManagedModule(long ModuleID, long AssemblyId, UTraceModuleFile ModuleFile) : UTraceModule(ModuleFile)
{
    /// <summary>
    /// Gets or sets the native module if available.
    /// </summary>
    public UTraceNativeModule? NativeModule { get; set; }
}