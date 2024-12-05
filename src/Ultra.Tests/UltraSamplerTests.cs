// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Sampler;
using Ultra.Sampler.MacOS;

namespace Ultra.Tests;

[TestClass]
public class UltraSamplerTests
{
    [TestMethod]
    public void TestNativeModules()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Inconclusive("This test is only for MacOS");

        var sampler = (MacOSUltraSampler)UltraSampler.Instance;
        var nativeModules = sampler.GetNativeModuleEvents();
        Assert.IsTrue(nativeModules.Length > 0);
        foreach (var nativeModule in nativeModules)
        {
            Console.WriteLine($"{nativeModule}");
        }
    }
}