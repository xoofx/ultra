// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a loaded module in the traced process.
/// </summary>
public sealed record UTraceNativeModule(UTraceModuleFile ModuleFile, UAddress BaseAddress, USize CodeSize) : UTraceModule(ModuleFile);