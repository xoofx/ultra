// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;

namespace Ultra.Tests;

[TestClass]
public class UltraEtwHardwareCounterTests
{
    // The sources of an AMD Zen 5 box (wpr -pmcsources), in part
    private static readonly List<UltraEtwProfileSource> Sources =
    [
        new("Timer", 0, 10000, 1221, 1000000),
        new("BranchInstructions", 6, 65536, 4096, int.MaxValue),
        new("DcacheMisses", 8, 65536, 4096, int.MaxValue),
        new("CacheMisses", 10, 65536, 4096, int.MaxValue),
        new("BranchMispredictions", 11, 65536, 4096, int.MaxValue),
        new("TotalCycles", 19, 65536, 4096, int.MaxValue),
        new("InstructionRetired", 25, 65536, 4096, int.MaxValue),
    ];

    [TestMethod]
    public void ParsesSourceNamesInAnyCaseWithDefaultIntervals()
    {
        Assert.AreEqual(new UltraEtwHardwareCounter("DcacheMisses", 8, 65536), UltraEtwHardwareCounter.Parse("dcachemisses", Sources));
        // A counter at about the clock rate gets a larger default interval
        Assert.AreEqual(new UltraEtwHardwareCounter("TotalCycles", 19, UltraEtwHardwareCounter.HighRateDefaultInterval), UltraEtwHardwareCounter.Parse("TotalCycles", Sources));
        Assert.AreEqual(new UltraEtwHardwareCounter("CacheMisses", 10, 20000), UltraEtwHardwareCounter.Parse("CacheMisses:20000", Sources));
    }

    [TestMethod]
    public void AcceptsLinuxNames()
    {
        Assert.AreEqual("CacheMisses", UltraEtwHardwareCounter.Parse("cache-misses", Sources).Name);
        Assert.AreEqual("DcacheMisses", UltraEtwHardwareCounter.Parse("L1-dcache-load-misses", Sources).Name);
        Assert.AreEqual("InstructionRetired", UltraEtwHardwareCounter.Parse("instructions", Sources).Name);
        Assert.AreEqual("BranchMispredictions", UltraEtwHardwareCounter.Parse("branch-misses:8192", Sources).Name);
    }

    [TestMethod]
    public void RejectsUnknownTimerBadIntervalAndDuplicates()
    {
        var unknown = Assert.ThrowsExactly<ArgumentException>(() => UltraEtwHardwareCounter.Parse("dTLB-load-misses", Sources));
        StringAssert.Contains(unknown.Message, "DcacheMisses");
        Assert.ThrowsExactly<ArgumentException>(() => UltraEtwHardwareCounter.Parse("Timer", Sources));
        Assert.ThrowsExactly<ArgumentException>(() => UltraEtwHardwareCounter.Parse("CacheMisses:100", Sources));
        Assert.ThrowsExactly<ArgumentException>(() => UltraEtwHardwareCounter.Parse("CacheMisses:abc", Sources));
        Assert.ThrowsExactly<ArgumentException>(() => UltraEtwHardwareCounter.ParseAll(["cache-misses", "CacheMisses"], Sources));
    }

    [TestMethod]
    public void ListShowsCountersAndAliasesButNotTheTimer()
    {
        var text = UltraEtwHardwareCounter.FormatList(Sources);
        StringAssert.Contains(text, "DcacheMisses");
        StringAssert.Contains(text, "cache-misses = CacheMisses");
        Assert.IsFalse(text.Contains("Timer"));
    }
}
