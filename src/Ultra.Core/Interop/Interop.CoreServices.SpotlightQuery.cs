// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    public static class SpotlightQuery
    {
        // Core Foundation / Core Services constants
        private const uint kCFStringEncodingUTF8 = 0x08000100;
        private const uint kMDQuerySynchronous = 1;

        #region CoreServices (Metadata) P/Invoke

        [DllImport(Interop.Libraries.CoreServicesLibrary)]
        private static extern SafeMDQueryHandle MDQueryCreate(
            IntPtr allocator,
            SafeCreateHandle queryString,
            SafeCFArrayHandle valueListAttrs,
            SafeCFArrayHandle sortingAttrs);

        [DllImport(Interop.Libraries.CoreServicesLibrary)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MDQueryExecute(
            SafeMDQueryHandle query,
            uint option);

        [DllImport(Interop.Libraries.CoreServicesLibrary)]
        private static extern nint MDQueryGetCount(
            SafeMDQueryHandle query);

        [DllImport(Interop.Libraries.CoreServicesLibrary)]
        private static extern IntPtr MDQueryGetResultAtIndex(
            SafeMDQueryHandle query,
            nint idx);

        #endregion

        // Additional APIs for extracting attributes from MDItem results:
        [DllImport(Interop.Libraries.CoreServicesLibrary)]
        private static extern SafeCFStringHandle MDItemCopyAttribute(
            IntPtr item,
            SafeCreateHandle name);

        public static void RunSpotlightQuery(string query)
        {
            var cfQuery = Interop.CoreFoundation.CFStringCreateWithCString(query);
            var mdQuery = MDQueryCreate(IntPtr.Zero, cfQuery, default, default);

            // Execute the query synchronously
            bool success = MDQueryExecute(mdQuery, kMDQuerySynchronous);
            if (!success)
            {
                Console.WriteLine("Failed to execute metadata query.");
                return;
            }

            nint count = MDQueryGetCount(mdQuery);
            Console.WriteLine($"Found {count} results for query: {query}");

            for (nint i = 0; i < count; i++)
            {
                IntPtr item = MDQueryGetResultAtIndex(mdQuery, i);
                // `item` is likely an MDItemRef (CFTypeRef), from which you can retrieve attributes.

                // For demonstration, retrieve the kMDItemPath attribute (file path).
                var cfKeyPath = Interop.CoreFoundation.CFStringCreateWithCString("kMDItemPath");
                var cfPathValue = MDItemCopyAttribute(item, cfKeyPath);
                if (!cfPathValue.IsInvalid)
                {
                    string path = Interop.CoreFoundation.CFStringToString(cfPathValue);
                    Console.WriteLine($"Result #{i + 1}: {path}");
                }
            }
        }
    }
}

