// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a frame in a call stack with a parent index and a code address index.
/// </summary>
public readonly record struct UCallStackFrame(UCallStackIndex ParentCallStackIndex, UCodeAddressIndex CodeAddressIndex);