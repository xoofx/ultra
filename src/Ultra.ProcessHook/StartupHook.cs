// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.
using System;
using System.Runtime.InteropServices;

/// <summary>
/// This class is used to load the native library <c>libUltraSamplerIndirect.dylib</c> on macOS ARM64
/// and allows to attach to an existing process by PID and profile it with ultra.
/// The native library is responsible for loading the native Ultra.Sampler.UltraSampler to profile the target process.
/// </summary>
// ReSharper disable once CheckNamespace
internal static class StartupHook
{
    private const string NativeLibraryName = "libUltraSamplerIndirect.dyld";
    private static nint _libraryHandle;

    public static void Initialize()
    {
        // We don't need to re-load the library once it is loaded.
        // Only load the library on macOS ARM64
        if (_libraryHandle == 0 && OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, NativeLibraryName), out _libraryHandle);
        }
    }
}
