// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static partial class Interop
{
    internal static partial class CoreFoundation
    {
        [LibraryImport(Libraries.CoreFoundationLibrary)]
        internal static partial IntPtr CFDictionaryGetValue(SafeCFDictionaryHandle handle, IntPtr key);
    }
}
