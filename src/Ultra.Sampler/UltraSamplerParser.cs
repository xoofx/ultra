// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Sampler;

public static class UltraSamplerParser
{
    public const string Name = "Ultra-Sampler";

    public const string IdAsString = "04E4DCBF-494F-4A77-B55E-F5C041A92F56";

    public static readonly Guid Id = BitConverter.IsLittleEndian ? new([
        0xBF, 0xDC, 0xE4, 0x04, // Data1 (reversed)
        0x4F, 0x49,             // Data2 (reversed)
        0x77, 0x4A,             // Data3 (reversed)
        0xB5, 0x5E,             // Data4 (unchanged)
        0xF5, 0xC0, 0x41, 0xA9, 0x2F, 0x56 // Data4 (unchanged)
    ]) : new([
        0x04, 0xE4, 0xDC, 0xBF, // Data1
        0x49, 0x4F,             // Data2
        0x4A, 0x77,             // Data3
        0xB5, 0x5E,             // Data4
        0xF5, 0xC0, 0x41, 0xA9, 0x2F, 0x56 // Data4
    ]);

    public const int NativeCallStackEvent = 1;

    public const int NativeModuleEvent = 2;


    public static void TestId(Guid guid)
    {
        guid = Id;
    }
}