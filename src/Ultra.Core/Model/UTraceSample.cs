// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a profile sample with call stack index, timestamp, and CPU time.
/// </summary>
public record struct UTraceSample(UCallStackIndex CallStackIndex, UTimeSpan Timestamp, UTimeSpan CpuTime);