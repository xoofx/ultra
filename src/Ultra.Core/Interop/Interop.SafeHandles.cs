// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    /// <summary>
    /// This class is a wrapper around the Create pattern in OS X where
    /// if a Create* function is called, the caller must also CFRelease
    /// on the same pointer in order to correctly free the memory.
    /// </summary>
    internal record struct SafeCreateHandle(nint Value) : ISafeHandle
    {
        public bool IsInvalid => Value == 0;

        public void Dispose()
        {
            if (Value != 0)
            {
                Interop.CoreFoundation.CFRelease(Value);
                Value = 0;
            }
        }
    }

    internal record struct SafeMDQueryHandle(nint Value) : ISafeHandle
    {
        public bool IsInvalid => Value == 0;

        public void Dispose()
        {
            if (Value != 0)
            {
                Interop.CoreFoundation.CFRelease(Value);
                Value = 0;
            }
        }
    }

    internal record struct SafeCFStringHandle(nint Value) : ISafeHandle
    {
        public bool IsInvalid => Value == 0;

        public void Dispose()
        {
            if (Value != 0)
            {
                Interop.CoreFoundation.CFRelease(Value);
                Value = 0;
            }
        }
    }

    internal record struct SafeCFArrayHandle(nint Value) : ISafeHandle
    {
        public bool IsInvalid => Value == 0;

        public void Dispose()
        {
            if (Value != 0)
            {
                Interop.CoreFoundation.CFRelease(Value);
                Value = 0;
            }
        }
    }

    internal record struct SafeCFDataHandle(nint Value) : ISafeHandle
    {
        public bool IsInvalid => Value == 0;

        public void Dispose()
        {
            if (Value != 0)
            {
                Interop.CoreFoundation.CFRelease(Value);
                Value = 0;
            }
        }
    }
    
    internal record struct SafeCFDateHandle(nint Value) : ISafeHandle
    {
        public bool IsInvalid => Value == 0;

        public void Dispose()
        {
            if (Value != 0)
            {
                Interop.CoreFoundation.CFRelease(Value);
                Value = 0;
            }
        }
    }

    internal record struct SafeCFErrorHandle(nint Value) : ISafeHandle
    {
        public bool IsInvalid => Value == 0;

        public void Dispose()
        {
            if (Value != 0)
            {
                Interop.CoreFoundation.CFRelease(Value);
                Value = 0;
            }
        }
    }

    internal record struct SafeCFDictionaryHandle(nint Value) : ISafeHandle
    {
        public bool IsInvalid => Value == 0;

        public void Dispose()
        {
            if (Value != 0)
            {
                Interop.CoreFoundation.CFRelease(Value);
                Value = 0;
            }
        }
    }

    internal interface ISafeHandle : IDisposable
    {
        nint Value { get; }
    }
}
