// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents an event that indicates the .NET garbage collector has restarted the execution engine.
/// </summary>
public sealed record GCRestartExecutionEngineTraceMarker : UTraceMarker;