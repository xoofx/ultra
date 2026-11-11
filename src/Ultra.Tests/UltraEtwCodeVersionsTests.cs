// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Ultra.Core;
using Event = Ultra.Core.UltraEtwFastFilter.CodeVersionEvent;

namespace Ultra.Tests;

/// <summary>
/// The code versions of a process (<see cref="UltraEtwCodeVersions"/>): which version a code address was in when it was sampled -
/// the memory of an unloaded method (a collectible assembly, a dynamic method) is used again by another.
/// </summary>
[TestClass]
public class UltraEtwCodeVersionsTests
{
    private const int Pid = 7;
    private const ulong X = 0x7FF8_1234_0000UL;

    private static UltraEtwFastFilter.CodeVersion At(double timeMs, Event what, long method, ulong start, int size, OptimizationTier tier)
        => new(Pid, start, size, tier, method, timeMs, what);

    private static (ulong Start, OptimizationTier Tier)? Find(UltraEtwCodeVersions versions, ulong address, double timeMs)
        => versions.TryFind(address, timeMs, out var start, out var tier) ? (start, tier) : null;

    /// <summary>
    /// A 256-byte Tier0 method is unloaded and a 512-byte Tier1 method gets its address: a sample is in the one that was there at
    /// its time, the second method has its whole code (its upper half too), and both stay known.
    /// </summary>
    [TestMethod]
    public void ARecycledAddressIsTheMethodThatWasThereAtTheTime()
    {
        var versions = new UltraEtwCodeVersions(
        [
            At(100, Event.RundownEnd, 2, X, 512, OptimizationTier.OptimizedTier1),   // out of order: the events are sorted
            At(10, Event.Load, 1, X, 256, OptimizationTier.QuickJitted),
            At(20, Event.Unload, 1, X, 256, OptimizationTier.QuickJitted),
            At(30, Event.Load, 2, X, 512, OptimizationTier.OptimizedTier1),
        ]);

        Assert.AreEqual(2, versions.Count, "two methods at one address, the second named twice");
        Assert.AreEqual((X, OptimizationTier.QuickJitted), Find(versions, X + 0x40, 15));
        Assert.AreEqual((X, OptimizationTier.QuickJitted), Find(versions, X + 0xFF, 19.9));
        Assert.AreEqual((X, OptimizationTier.OptimizedTier1), Find(versions, X + 0x40, 35));
        Assert.AreEqual((X, OptimizationTier.OptimizedTier1), Find(versions, X + 0x180, 35), "the upper half of the second method");
        Assert.AreEqual((X, OptimizationTier.OptimizedTier1), Find(versions, X + 0x1FF, 5000));
        Assert.IsNull(Find(versions, X + 0x200, 35), "past the end of both");
        Assert.IsNull(Find(versions, X - 1, 35));

        // between the two (the address was free) and outside the code of the one that was there: the nearest in time that holds it
        Assert.AreEqual((X, OptimizationTier.QuickJitted), Find(versions, X + 0x40, 22));
        Assert.AreEqual((X, OptimizationTier.OptimizedTier1), Find(versions, X + 0x40, 29));
        Assert.AreEqual((X, OptimizationTier.OptimizedTier1), Find(versions, X + 0x180, 15));
    }

    /// <summary>The same when the unload was not seen (a lost event): the first method ended when the second one loaded.</summary>
    [TestMethod]
    public void ASecondMethodAtAnAddressEndsTheFirstWithoutItsUnload()
    {
        var versions = new UltraEtwCodeVersions(
        [
            At(10, Event.Load, 1, X, 256, OptimizationTier.QuickJitted),
            At(30, Event.Load, 2, X, 512, OptimizationTier.OptimizedTier1),
        ]);

        Assert.AreEqual(2, versions.Count);
        Assert.AreEqual((X, OptimizationTier.QuickJitted), Find(versions, X + 0x40, 15));
        Assert.AreEqual((X, OptimizationTier.OptimizedTier1), Find(versions, X + 0x40, 35));
        Assert.AreEqual((X, OptimizationTier.OptimizedTier1), Find(versions, X + 0x180, 35));
    }

    /// <summary>
    /// A version named at its JIT and again by the rundown is one, with the tier one of them knows; a version only the rundown
    /// names (compiled before the profiling) was there for the whole trace.
    /// </summary>
    [TestMethod]
    public void AVersionNamedTwiceIsOneAndARundownOnlyOneWasAlwaysThere()
    {
        var versions = new UltraEtwCodeVersions(
        [
            At(10, Event.Load, 1, X, 256, OptimizationTier.Unknown),
            At(900, Event.RundownEnd, 1, X, 256, OptimizationTier.OptimizedTier1OSR),
            At(900, Event.RundownEnd, 2, X + 0x1000, 64, OptimizationTier.OptimizedTier1),
            At(1, Event.RundownStart, 3, X + 0x2000, 64, OptimizationTier.QuickJitted),
            At(900, Event.RundownEnd, 3, X + 0x2000, 64, OptimizationTier.QuickJitted),
        ]);

        Assert.AreEqual(3, versions.Count);
        Assert.AreEqual((X, OptimizationTier.OptimizedTier1OSR), Find(versions, X + 0x80, 500));
        Assert.AreEqual((X + 0x1000, OptimizationTier.OptimizedTier1), Find(versions, X + 0x1010, 0));
        Assert.AreEqual((X + 0x1000, OptimizationTier.OptimizedTier1), Find(versions, X + 0x1010, 500));
        Assert.AreEqual((X + 0x2000, OptimizationTier.QuickJitted), Find(versions, X + 0x2010, 0.5));
        Assert.IsNull(Find(versions, X + 0x1040, 500), "the first address past a version's code");
        Assert.IsNull(Find(versions, X + 0x800, 500), "between two versions");
        Assert.IsFalse(UltraEtwCodeVersions.Empty.TryFind(X, 0, out _, out _));
    }

    /// <summary>
    /// A method that was there before the profiling and is unloaded during it has no load event: it is there until its unload, and
    /// the method the rundown names at its address after it.
    /// </summary>
    [TestMethod]
    public void AMethodUnloadedWithoutALoadWasThereUntilItsUnload()
    {
        var versions = new UltraEtwCodeVersions(
        [
            At(20, Event.Unload, 1, X, 256, OptimizationTier.OptimizedTier1),
            At(900, Event.RundownEnd, 2, X, 512, OptimizationTier.QuickJitted),
        ]);

        Assert.AreEqual(2, versions.Count);
        Assert.AreEqual((X, OptimizationTier.OptimizedTier1), Find(versions, X + 0x40, 5));
        Assert.AreEqual((X, OptimizationTier.QuickJitted), Find(versions, X + 0x40, 25));
        Assert.AreEqual((X, OptimizationTier.QuickJitted), Find(versions, X + 0x180, 25));
    }

    /// <summary>The memory used again does not have to start at the same address: a new method inside the code of an unloaded one.</summary>
    [TestMethod]
    public void AMethodInsideTheMemoryOfAnUnloadedOne()
    {
        var versions = new UltraEtwCodeVersions(
        [
            At(20, Event.Unload, 1, X, 0x200, OptimizationTier.OptimizedTier1),
            At(30, Event.Load, 2, X + 0x100, 0x80, OptimizationTier.QuickJitted),
            At(900, Event.RundownEnd, 4, X - 0x1000, 0x40, OptimizationTier.OptimizedTier1),
        ]);

        Assert.AreEqual((X, OptimizationTier.OptimizedTier1), Find(versions, X + 0x120, 10), "the old method, though a later one starts nearer");
        Assert.AreEqual((X + 0x100, OptimizationTier.QuickJitted), Find(versions, X + 0x120, 40));
        Assert.AreEqual((X, OptimizationTier.OptimizedTier1), Find(versions, X + 0x1A0, 10));
        Assert.AreEqual((X, OptimizationTier.OptimizedTier1), Find(versions, X + 0x1A0, 40), "only the old one holds it: the nearest in time");
        Assert.AreEqual((X - 0x1000, OptimizationTier.OptimizedTier1), Find(versions, X - 0x1000 + 0x3F, 40));
        Assert.IsNull(Find(versions, X - 0x800, 40));
    }
}
