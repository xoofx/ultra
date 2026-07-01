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
        /// <summary>
        /// Returns the interior pointer of the cfString if it has the specified encoding.
        /// If it has the wrong encoding, or if the interior pointer isn't being shared for some reason, returns NULL
        /// </summary>
        [LibraryImport(Libraries.CoreFoundationLibrary)]
        private static partial IntPtr CFStringGetCStringPtr(
            SafeCFStringHandle cfString,
            CFStringBuiltInEncodings encoding);

        [LibraryImport(Libraries.CoreFoundationLibrary)]
        private static partial SafeCFDataHandle CFStringCreateExternalRepresentation(
            IntPtr alloc,
            SafeCFStringHandle theString,
            CFStringBuiltInEncodings encoding,
            byte lossByte);

        internal static string CFStringToString(SafeCFStringHandle cfString)
        {
            Debug.Assert(!cfString.IsInvalid);

            // If the string is already stored internally as UTF-8 we can (usually)
            // get the raw pointer to the data blob, then we can Marshal in the string
            // via pointer semantics, avoiding a copy.
            IntPtr interiorPointer = CFStringGetCStringPtr(
                cfString,
                CFStringBuiltInEncodings.kCFStringEncodingUTF8);

            if (interiorPointer != IntPtr.Zero)
            {
                return Marshal.PtrToStringUTF8(interiorPointer)!;
            }

            SafeCFDataHandle cfData = CFStringCreateExternalRepresentation(
                IntPtr.Zero,
                cfString,
                CFStringBuiltInEncodings.kCFStringEncodingUTF8,
                0);

            using (cfData)
            {
                unsafe
                {
                    // Note that CFDataGetLength(cfData).ToInt32() will throw on
                    // too large of an input. Since a >2GB string is pretty unlikely,
                    // that's considered a good thing here.
                    return Encoding.UTF8.GetString(
                        CFDataGetBytePtr(cfData),
                        CFDataGetLength(cfData).ToInt32());
                }
            }
        }
    }
}
