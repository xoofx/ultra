// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core;

/// <summary>
/// Represents a trace file used by UltraProfiler.
/// </summary>
/// <param name="FileName">The name of the trace file.</param>
public readonly record struct UltraProfilerTraceFile(string FileName)
{
    /// <summary>
    /// Gets a value indicating whether the trace file is an ETL file.
    /// </summary>
    public bool IsEtl => FileName.EndsWith(".etl", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a value indicating whether the trace file is a Nettrace file.
    /// </summary>
    public bool IsNettrace => FileName.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase);
}