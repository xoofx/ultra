// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core.Model;

/// <summary>
/// Represents a method in a traced process, including the method's full name, start address, and size.
/// </summary>
public record UTraceMethod(string FullName, UAddress MethodStartAddress, USize MethodSize);