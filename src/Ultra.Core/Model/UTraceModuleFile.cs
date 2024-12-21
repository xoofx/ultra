// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a module file that is loaded in the traced process.
/// </summary>
public record UTraceModuleFile(string FilePath)
{
    /// <summary>
    /// Gets or sets the module file index.
    /// </summary>
    public UTraceModuleFileIndex Index { get; internal set; } = UTraceModuleFileIndex.Invalid;

    /// <summary>
    /// Gets or sets the symbol UUID for the module file.
    /// </summary>
    public Guid SymbolUuid { get; set; }

    /// <summary>
    /// Gets or sets the symbol file path for the module.
    /// </summary>
    public string? SymbolFilePath { get; set; }

    /// <summary>
    /// Gets or sets the load time of the module. Time is relative to the start of the session.
    /// </summary>
    public UTimeSpan LoadTime { get; set; }

    /// <summary>
    /// Gets or sets the unload time of the module. Time is relative to the start of the session.
    /// </summary>
    public UTimeSpan UnloadTime { get; set; }
}