// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core;

/// <summary>
/// The mode of the console output.
/// </summary>
public enum EtwUltraProfilerConsoleMode
{
    /// <summary>
    /// No console output from the program started.
    /// </summary>
    Silent,

    /// <summary>
    /// Redirect the console output from the program started to the current console, but live progress using Spectre.Console is disabled.
    /// </summary>
    Raw,

    /// <summary>
    /// Redirect the last lines of the console output from the program started to the live progress using Spectre.Console.
    /// </summary>
    Live,
}