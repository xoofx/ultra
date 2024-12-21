// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Ultra.Core.Model;

/// <summary>
/// Represents a managed method in a traced process.
/// </summary>
public record UTraceManagedMethod(int ThreadID, long ModuleID, long MethodID, string MethodNamespace, string MethodName, string MethodSignature, int MethodToken, MethodFlags MethodFlags, UAddress MethodStartAddress, USize MethodSize) : UTraceMethod(CreateFullName(MethodNamespace, MethodName), MethodStartAddress, MethodSize)
{
    /// <summary>
    /// Gets or sets the native IL offsets for the method.
    /// </summary>
    public UNativeILOffset[]? ILToNativeILOffsets { get; set; }

    private static string CreateFullName(string methodNamespace, string methodName) => string.IsNullOrEmpty(methodNamespace) ? methodName : $"{methodNamespace}.{methodName}";
}