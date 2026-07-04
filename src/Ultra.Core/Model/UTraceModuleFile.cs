// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a module file that is loaded in the traced process.
/// </summary>
public sealed record UTraceModuleFile(string FilePath)
{
    private string? _fileName;

    /// <summary>
    /// Gets the file name (without the directory) of the module.
    /// </summary>
    public string FileName => _fileName ??= Path.GetFileName(FilePath);

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
}