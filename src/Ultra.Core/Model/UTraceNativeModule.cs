// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a loaded module in the traced process.
/// </summary>
public sealed record UTraceNativeModule(UTraceModuleFile ModuleFile, UAddress BaseAddress, USize CodeSize) : UTraceModule(ModuleFile)
{
    /// <summary>
    /// Gets or sets the offset in the module file of the byte mapped at <see cref="BaseAddress"/> (Linux: the executable
    /// segment of an ELF file is mapped separately from its start). Zero when the whole image is mapped from its start.
    /// </summary>
    public ulong FileOffset { get; set; }
}