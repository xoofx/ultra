// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Ultra.Sampler;

namespace Ultra.Tests;

[SuppressMessage("ReSharper", "InconsistentNaming")]
[TestClass]
public class libSystemTests
{
    [TestMethod]
    public unsafe void TestStructSize()
    {
        if (IntPtr.Size != 8) Assert.Inconclusive("Cannot run this test on 32-bit");

        Assert.AreEqual(368, sizeof(libSystem.dyld_all_image_infos));
        Assert.AreEqual(24, sizeof(libSystem.dyld_image_info));

        libSystem.dyld_all_image_infos infos = default;
        nint offset = Unsafe.ByteOffset(ref Unsafe.As<libSystem.dyld_all_image_infos, byte>(ref infos), ref Unsafe.As<ulong, byte>(ref Unsafe.AsRef(in infos.infoArrayChangeTimestamp)));
        Assert.AreEqual(184, offset);
    }
}