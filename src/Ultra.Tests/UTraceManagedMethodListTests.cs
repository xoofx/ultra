// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Ultra.Core.Model;

namespace Ultra.Tests;

/// <summary>
/// Tests for <see cref="UTraceManagedMethodList"/>, mainly around collectible (dynamic) methods whose code
/// block is freed by the runtime and recycled for another method at the same address.
/// </summary>
[TestClass]
public class UTraceManagedMethodListTests
{
    private const ulong CodeBlock = 0x10FE80040;

    [TestMethod]
    public void RecycledCodeBlockResolvesToTheMethodLoadedAtSampleTime()
    {
        var methods = new UTraceManagedMethodList();

        Load(methods, methodID: 1, "InvokeStub_EventSourceAttribute.set_Name", CodeBlock, size: 0x38, loadTimeMs: 10);
        Unload(methods, methodID: 1, CodeBlock, unloadTimeMs: 20);
        Load(methods, methodID: 2, "lambda_method4", CodeBlock, size: 0xC4, loadTimeMs: 30);

        methods.SortMethodAddressRanges();

        AssertMethodAt(methods, CodeBlock, timeMs: 15, "InvokeStub_EventSourceAttribute.set_Name");
        AssertMethodAt(methods, CodeBlock, timeMs: 40, "lambda_method4");
        // An address inside the code block, and not only its first address, resolves as well
        AssertMethodAt(methods, CodeBlock + 0x30, timeMs: 40, "lambda_method4");
    }

    [TestMethod]
    public void RecycledCodeBlockWithoutUnloadEventResolvesToTheMethodLoadedAtSampleTime()
    {
        var methods = new UTraceManagedMethodList();

        // The unload event can be missing from the trace: the previous occupant must still be closed
        // when another method is loaded at the same address.
        Load(methods, methodID: 1, "httpGetter", CodeBlock, size: 0x2C, loadTimeMs: 10);
        Load(methods, methodID: 2, "defaultHealthyRetryPolicyGetter", CodeBlock, size: 0x54, loadTimeMs: 30);

        methods.SortMethodAddressRanges();

        AssertMethodAt(methods, CodeBlock, timeMs: 15, "httpGetter");
        AssertMethodAt(methods, CodeBlock, timeMs: 40, "defaultHealthyRetryPolicyGetter");
    }

    [TestMethod]
    public void RecycledCodeBlockDoesNotThrow()
    {
        // Regression test: adding the address of a recycled code block used to throw
        // `Invalid key 0x... duplicated` from the underlying dictionary.
        var methods = new UTraceManagedMethodList();

        for (var i = 0; i < 10; i++)
        {
            Load(methods, methodID: i, $"lambda_method{i}", CodeBlock, size: 0x20 + (ulong)i, loadTimeMs: i * 10);
            Unload(methods, methodID: i, CodeBlock, unloadTimeMs: i * 10 + 5);
        }

        methods.SortMethodAddressRanges();

        Assert.AreEqual(10, methods.Count);
        AssertMethodAt(methods, CodeBlock, timeMs: 52, "lambda_method5");
    }

    [TestMethod]
    public void RecompiledMethodKeepsAllItsCodeRanges()
    {
        var methods = new UTraceManagedMethodList();

        // Tiered compilation: the same method is recompiled at another address, both ranges stay valid.
        Load(methods, methodID: 1, "Compute", CodeBlock, size: 0x40, loadTimeMs: 10);
        Load(methods, methodID: 1, "Compute", CodeBlock + 0x1000, size: 0x80, loadTimeMs: 20);

        methods.SortMethodAddressRanges();

        Assert.AreEqual(1, methods.Count);
        AssertMethodAt(methods, CodeBlock, timeMs: 15, "Compute");
        AssertMethodAt(methods, CodeBlock + 0x1000, timeMs: 25, "Compute");
    }

    [TestMethod]
    public void MethodOnlyReportedByARundownEventResolvesAtAnyTime()
    {
        var methods = new UTraceManagedMethodList();

        // A rundown event carries no meaningful load time, the method must resolve for earlier samples too.
        Load(methods, methodID: 1, "Compute", CodeBlock, size: 0x40, loadTimeMs: 0);

        methods.SortMethodAddressRanges();

        AssertMethodAt(methods, CodeBlock, timeMs: 0, "Compute");
        AssertMethodAt(methods, CodeBlock, timeMs: 1000, "Compute");
    }

    [TestMethod]
    public void UnknownAddressIsNotResolved()
    {
        var methods = new UTraceManagedMethodList();

        Load(methods, methodID: 1, "Compute", CodeBlock, size: 0x40, loadTimeMs: 10);
        methods.SortMethodAddressRanges();

        Assert.IsFalse(methods.TryFindMethodByAddress(CodeBlock + 0x40, UTimeSpan.FromMilliseconds(15), out _));
        Assert.IsFalse(methods.TryFindMethodIndexByAddress(CodeBlock - 1, UTimeSpan.FromMilliseconds(15), out var methodIndex));
        Assert.AreEqual(-1, methodIndex);
    }

    /// <summary>
    /// The runtime gives the id of a collected dynamic method (its method descriptor) to a later one: a known id with another
    /// name is another method, wherever its code is - also at the address of the first one.
    /// </summary>
    [TestMethod]
    public void ARecycledMethodIdIsAnotherMethod()
    {
        var methods = new UTraceManagedMethodList();

        Load(methods, methodID: 1, "dyn_0", CodeBlock, size: 0x40, loadTimeMs: 10, OptimizationTier.QuickJitted);
        UnloadEvent(methods, methodID: 1, "dyn_0", CodeBlock, size: 0x40, unloadTimeMs: 20);
        Load(methods, methodID: 1, "dyn_1", CodeBlock + 0x1000, size: 0x40, loadTimeMs: 30, OptimizationTier.OptimizedTier1);
        UnloadEvent(methods, methodID: 1, "dyn_1", CodeBlock + 0x1000, size: 0x40, unloadTimeMs: 40);
        Load(methods, methodID: 1, "dyn_2", CodeBlock, size: 0x80, loadTimeMs: 50, OptimizationTier.OptimizedTier1);

        Assert.AreEqual(3, methods.Count);
        AssertMethodAt(methods, CodeBlock + 0x10, timeMs: 15, "dyn_0", CodeBlock, OptimizationTier.QuickJitted);
        AssertMethodAt(methods, CodeBlock + 0x1010, timeMs: 35, "dyn_1", CodeBlock + 0x1000, OptimizationTier.OptimizedTier1);
        AssertMethodAt(methods, CodeBlock + 0x10, timeMs: 55, "dyn_2", CodeBlock, OptimizationTier.OptimizedTier1);
        AssertMethodAt(methods, CodeBlock + 0x60, timeMs: 55, "dyn_2", CodeBlock, OptimizationTier.OptimizedTier1);
        Assert.IsTrue(methods.TryFindMethodById(1, out var last));
        Assert.AreEqual("dyn_2", last.MethodName, "the id is the last method's that had it");
    }

    /// <summary>
    /// Dynamic methods of one name (e.g. all "lambda_method") cannot be told apart by an event: they stay one method, but the
    /// code that comes back to an address after its unload is another range, with its own tier and size.
    /// </summary>
    [TestMethod]
    public void AMethodThatComesBackToItsAddressAfterItsUnloadIsThereAgain()
    {
        var methods = new UTraceManagedMethodList();

        Load(methods, methodID: 1, "lambda_method", CodeBlock, size: 0x40, loadTimeMs: 10, OptimizationTier.QuickJitted);
        UnloadEvent(methods, methodID: 1, "lambda_method", CodeBlock, size: 0x40, unloadTimeMs: 20);
        Load(methods, methodID: 1, "lambda_method", CodeBlock, size: 0x80, loadTimeMs: 30, OptimizationTier.OptimizedTier1);

        Assert.AreEqual(1, methods.Count);
        AssertMethodAt(methods, CodeBlock + 0x60, timeMs: 35, "lambda_method", CodeBlock, OptimizationTier.OptimizedTier1);
        Assert.IsFalse(methods.TryFindMethodByAddress(CodeBlock + 0x80, UTimeSpan.FromMilliseconds(35), out _));
    }

    /// <summary>
    /// The recycled memory does not have to start at the same address: a method loaded inside the code of an unloaded one. The
    /// samples of the old one, taken before, are still its own - also past the start of the new one.
    /// </summary>
    [TestMethod]
    public void AMethodInsideTheMemoryOfAnUnloadedOne()
    {
        var methods = new UTraceManagedMethodList();

        Rundown(methods, methodID: 10, "Below", CodeBlock - 0x1000, size: 0x40, rundownTimeMs: 900);
        Load(methods, methodID: 1, "Old", CodeBlock, size: 0x200, loadTimeMs: 5, OptimizationTier.OptimizedTier1);
        UnloadEvent(methods, methodID: 1, "Old", CodeBlock, size: 0x200, unloadTimeMs: 20);
        Load(methods, methodID: 2, "New", CodeBlock + 0x100, size: 0x80, loadTimeMs: 30, OptimizationTier.QuickJitted);
        Rundown(methods, methodID: 11, "Above", CodeBlock + 0x1000, size: 0x40, rundownTimeMs: 900);
        Rundown(methods, methodID: 12, "Above2", CodeBlock + 0x2000, size: 0x40, rundownTimeMs: 900);

        AssertMethodAt(methods, CodeBlock + 0x120, timeMs: 10, "Old", CodeBlock, OptimizationTier.OptimizedTier1);
        AssertMethodAt(methods, CodeBlock + 0x120, timeMs: 40, "New", CodeBlock + 0x100, OptimizationTier.QuickJitted);
        AssertMethodAt(methods, CodeBlock + 0x1A0, timeMs: 10, "Old", CodeBlock, OptimizationTier.OptimizedTier1);
        AssertMethodAt(methods, CodeBlock + 0x40, timeMs: 10, "Old", CodeBlock, OptimizationTier.OptimizedTier1);
        Assert.IsFalse(methods.TryFindMethodByAddress(CodeBlock + 0x200, UTimeSpan.FromMilliseconds(10), out _));
        Assert.IsFalse(methods.TryFindMethodByAddress(CodeBlock - 0x800, UTimeSpan.FromMilliseconds(10), out _));
    }

    /// <summary>Three methods at one address, a small one between two large ones: an address past the small one's code is the large one's that was there.</summary>
    [TestMethod]
    public void ASmallMethodBetweenTwoLargeOnesAtOneAddress()
    {
        var methods = new UTraceManagedMethodList();

        Load(methods, methodID: 1, "Big1", CodeBlock, size: 0x200, loadTimeMs: 10, OptimizationTier.QuickJitted);
        UnloadEvent(methods, methodID: 1, "Big1", CodeBlock, size: 0x200, unloadTimeMs: 20);
        Load(methods, methodID: 2, "Small", CodeBlock, size: 0x100, loadTimeMs: 30, OptimizationTier.QuickJitted);
        UnloadEvent(methods, methodID: 2, "Small", CodeBlock, size: 0x100, unloadTimeMs: 40);
        Load(methods, methodID: 3, "Big2", CodeBlock, size: 0x200, loadTimeMs: 50, OptimizationTier.OptimizedTier1);

        AssertMethodAt(methods, CodeBlock + 0x180, timeMs: 15, "Big1", CodeBlock, OptimizationTier.QuickJitted);
        AssertMethodAt(methods, CodeBlock + 0x40, timeMs: 35, "Small", CodeBlock, OptimizationTier.QuickJitted);
        AssertMethodAt(methods, CodeBlock + 0x180, timeMs: 55, "Big2", CodeBlock, OptimizationTier.OptimizedTier1);
        // while the address was free: the nearest in time that held it
        AssertMethodAt(methods, CodeBlock + 0x180, timeMs: 22, "Big1", CodeBlock, OptimizationTier.QuickJitted);
        AssertMethodAt(methods, CodeBlock + 0x180, timeMs: 48, "Big2", CodeBlock, OptimizationTier.OptimizedTier1);
    }

    /// <summary>
    /// A method only the rundown at the end names, at the address of one that was not seen to unload (lost events): the first
    /// one keeps its samples, it ended at the latest at the rundown.
    /// </summary>
    [TestMethod]
    public void AMethodOnlyTheRundownNamesDoesNotTakeTheSamplesOfTheOneBeforeIt()
    {
        var methods = new UTraceManagedMethodList();

        Load(methods, methodID: 1, "First", CodeBlock, size: 0x100, loadTimeMs: 10, OptimizationTier.QuickJitted);
        Rundown(methods, methodID: 2, "Second", CodeBlock, size: 0x200, rundownTimeMs: 900, OptimizationTier.OptimizedTier1);

        AssertMethodAt(methods, CodeBlock + 0x40, timeMs: 15, "First", CodeBlock, OptimizationTier.QuickJitted);
        AssertMethodAt(methods, CodeBlock + 0x180, timeMs: 15, "Second", CodeBlock, OptimizationTier.OptimizedTier1);
        AssertMethodAt(methods, CodeBlock + 0x40, timeMs: 950, "Second", CodeBlock, OptimizationTier.OptimizedTier1);
    }

    /// <summary>
    /// A method that was there before the events were enabled has no load event: its unload event records it as there until
    /// then, and the one the rundown names at its address as there since. The same unload told twice changes nothing.
    /// </summary>
    [TestMethod]
    public void AMethodUnloadedWithoutALoadWasThereUntilItsUnload()
    {
        var methods = new UTraceManagedMethodList();

        UnloadEvent(methods, methodID: 1, "Before", CodeBlock, size: 0x100, unloadTimeMs: 20, OptimizationTier.OptimizedTier1);
        UnloadEvent(methods, methodID: 1, "Before", CodeBlock, size: 0x100, unloadTimeMs: 20, OptimizationTier.OptimizedTier1);
        Rundown(methods, methodID: 2, "After", CodeBlock, size: 0x200, rundownTimeMs: 900, OptimizationTier.QuickJitted);
        Rundown(methods, methodID: 2, "After", CodeBlock, size: 0x200, rundownTimeMs: 900, OptimizationTier.QuickJitted);

        Assert.AreEqual(2, methods.Count);
        AssertMethodAt(methods, CodeBlock + 0x40, timeMs: 5, "Before", CodeBlock, OptimizationTier.OptimizedTier1);
        AssertMethodAt(methods, CodeBlock + 0x40, timeMs: 25, "After", CodeBlock, OptimizationTier.QuickJitted);
        AssertMethodAt(methods, CodeBlock + 0x180, timeMs: 25, "After", CodeBlock, OptimizationTier.QuickJitted);
    }

    /// <summary>
    /// A method that comes back to its address after its unload - same id, same name, another compilation - is one method with
    /// two code versions there: a sample takes the version that was loaded at its time (Tier0 at 15 ms, not the later Tier1), and
    /// the code address of its frame keeps that version through the conversion.
    /// </summary>
    [TestMethod]
    public void ASampleTakesTheCodeVersionThatWasThereAtItsTime()
    {
        var methods = new UTraceManagedMethodList();

        Load(methods, methodID: 1, "lambda_method", CodeBlock, size: 0x40, loadTimeMs: 10, OptimizationTier.QuickJitted);
        UnloadEvent(methods, methodID: 1, "lambda_method", CodeBlock, size: 0x40, unloadTimeMs: 20);
        Load(methods, methodID: 1, "lambda_method", CodeBlock, size: 0x80, loadTimeMs: 30, OptimizationTier.OptimizedTier1);

        UAddress address = CodeBlock + 0x10;
        Assert.IsTrue(methods.TryFindCodeRangeByAddress(address, UTimeSpan.FromMilliseconds(15), out var early));
        Assert.IsTrue(methods.TryFindCodeRangeByAddress(address, UTimeSpan.FromMilliseconds(35), out var late));
        Assert.AreEqual(early.Index, late.Index, "one method");
        Assert.AreEqual(OptimizationTier.QuickJitted, early.Tier);
        Assert.AreEqual((UAddress)CodeBlock, early.BeginAddress);
        Assert.AreEqual((UAddress)(CodeBlock + 0x40), early.EndAddress);
        Assert.AreEqual(OptimizationTier.OptimizedTier1, late.Tier);
        Assert.AreEqual((UAddress)(CodeBlock + 0x80), late.EndAddress);
        Assert.IsFalse(methods.TryFindCodeRangeByAddress(CodeBlock + 0x80, UTimeSpan.FromMilliseconds(35), out _));

        // as the sample recorders do it: the code address of a frame is the address in the version found at the sample's time
        var codeAddresses = new UCodeAddressList();
        var earlyFrame = codeAddresses.GetOrCreateAddress(address, early);
        var lateFrame = codeAddresses.GetOrCreateAddress(address, late);
        Assert.AreNotEqual(earlyFrame, lateFrame, "one address, one method, two versions: two code addresses");
        Assert.AreEqual(earlyFrame, codeAddresses.GetOrCreateAddress(address, early));
        Assert.AreEqual(early.Index, codeAddresses.GetManagedMethodIndex(earlyFrame));
        Assert.AreEqual(late.Index, codeAddresses.GetManagedMethodIndex(lateFrame));

        // and the converter reads the version back from the code address, not from the method (whose latest range is Tier1)
        Assert.IsTrue(codeAddresses.TryGetCodeVersion(earlyFrame, out var codeStart, out var tier));
        Assert.AreEqual((UAddress)CodeBlock, codeStart);
        Assert.AreEqual(OptimizationTier.QuickJitted, tier);
        Assert.IsTrue(codeAddresses.TryGetCodeVersion(lateFrame, out codeStart, out tier));
        Assert.AreEqual(OptimizationTier.OptimizedTier1, tier);
        Assert.IsTrue(methods.TryFindMethodCodeStart(address, early.Index, out _, out tier));
        Assert.AreEqual(OptimizationTier.OptimizedTier1, tier, "without a time: the method's most recent range");

        // a native address, and a code address created without its range, have no version
        var native = codeAddresses.GetOrCreateAddress(0x400000UL, -1);
        Assert.IsFalse(codeAddresses.TryGetCodeVersion(native, out _, out _));
        Assert.AreEqual(-1, codeAddresses.GetManagedMethodIndex(native));
        var withoutRange = codeAddresses.GetOrCreateAddress(address, early.Index);
        Assert.AreNotEqual(earlyFrame, withoutRange);
        Assert.IsFalse(codeAddresses.TryGetCodeVersion(withoutRange, out _, out _));
        Assert.AreEqual(4, codeAddresses.Count);
    }

    /// <summary>
    /// A later report of a method at its address can be larger (a load event, then a rundown or an unload with another size):
    /// the range grows, and after a lookup or a sort the addresses it gained must be found too.
    /// </summary>
    [TestMethod]
    public void ARangeThatGrowsAfterALookupIsFoundToItsNewEnd()
    {
        var methods = new UTraceManagedMethodList();

        Load(methods, methodID: 1, "Compute", 0x1000UL, size: 0x40, loadTimeMs: 10);
        Load(methods, methodID: 2, "Other", 0x3000UL, size: 0x40, loadTimeMs: 10);
        AssertMethodAt(methods, 0x1010UL, timeMs: 15, "Compute");
        Assert.IsFalse(methods.TryFindMethodByAddress(0x1060UL, UTimeSpan.FromMilliseconds(15), out _));
        methods.SortMethodAddressRanges();

        // the same method reported again at its address, 0x80 bytes now
        Rundown(methods, methodID: 1, "Compute", 0x1000UL, size: 0x80, rundownTimeMs: 900);
        AssertMethodAt(methods, 0x1060UL, timeMs: 15, "Compute");
        Assert.IsTrue(methods.TryFindMethodIndexByAddress(0x1060UL, UTimeSpan.FromMilliseconds(15), out var compute));
        Assert.IsTrue(methods.TryFindMethodCodeStart(0x1060UL, compute, out var codeStart));
        Assert.AreEqual((UAddress)0x1000UL, codeStart);
        Assert.IsFalse(methods.TryFindMethodByAddress(0x1080UL, UTimeSpan.FromMilliseconds(15), out _));

        // and by its unload event
        UnloadEvent(methods, methodID: 2, "Other", 0x3000UL, size: 0x100, unloadTimeMs: 50);
        AssertMethodAt(methods, 0x30F0UL, timeMs: 15, "Other");
        Assert.IsFalse(methods.TryFindMethodByAddress(0x3100UL, UTimeSpan.FromMilliseconds(15), out _));
    }

    /// <summary>A lookup sorts the ranges when one was added since the last sort, and a later event meets the right range of its address.</summary>
    [TestMethod]
    public void RangesAddedAfterALookupAreFound()
    {
        var methods = new UTraceManagedMethodList();

        Load(methods, methodID: 2, "Second", CodeBlock + 0x1000, size: 0x40, loadTimeMs: 10);
        Load(methods, methodID: 1, "First", CodeBlock, size: 0x40, loadTimeMs: 10);
        AssertMethodAt(methods, CodeBlock + 0x10, timeMs: 15, "First");

        // after the sort the ranges have moved: the unload must still close the one of its address
        UnloadEvent(methods, methodID: 2, "Second", CodeBlock + 0x1000, size: 0x40, unloadTimeMs: 20);
        Load(methods, methodID: 3, "Third", CodeBlock + 0x1000, size: 0x40, loadTimeMs: 30);
        AssertMethodAt(methods, CodeBlock + 0x1010, timeMs: 15, "Second");
        AssertMethodAt(methods, CodeBlock + 0x1010, timeMs: 35, "Third");
        AssertMethodAt(methods, CodeBlock + 0x10, timeMs: 35, "First");
    }

    private static void Load(UTraceManagedMethodList methods, long methodID, string methodName, UAddress address, ulong size, double loadTimeMs, OptimizationTier tier)
        => methods.GetOrCreateManagedMethod(1, 1, methodID, "dynamicClass", methodName, "()", 0, MethodFlags.Dynamic | MethodFlags.Jitted, address, new USize(size), UTimeSpan.FromMilliseconds(loadTimeMs), tier);

    // as UltraEventProcessor registers a rundown event: no load time, the time of the rundown
    private static void Rundown(UTraceManagedMethodList methods, long methodID, string methodName, UAddress address, ulong size, double rundownTimeMs, OptimizationTier tier = OptimizationTier.Unknown)
        => methods.GetOrCreateManagedMethod(1, 1, methodID, "dynamicClass", methodName, "()", 0, MethodFlags.Dynamic | MethodFlags.Jitted, address, new USize(size), default, tier, UTimeSpan.FromMilliseconds(rundownTimeMs));

    // as UltraEventProcessor records an unload event
    private static void UnloadEvent(UTraceManagedMethodList methods, long methodID, string methodName, UAddress address, ulong size, double unloadTimeMs, OptimizationTier tier = OptimizationTier.Unknown)
        => methods.UnloadManagedMethod(1, 1, methodID, "dynamicClass", methodName, "()", 0, MethodFlags.Dynamic | MethodFlags.Jitted, address, new USize(size), UTimeSpan.FromMilliseconds(unloadTimeMs), tier);

    private static void AssertMethodAt(UTraceManagedMethodList methods, UAddress address, double timeMs, string expectedName, UAddress expectedCodeStart, OptimizationTier expectedTier)
    {
        Assert.IsTrue(methods.TryFindMethodIndexByAddress(address, UTimeSpan.FromMilliseconds(timeMs), out var methodIndex), $"No method found at {address} at {timeMs}ms");
        Assert.AreEqual(expectedName, methods[methodIndex].MethodName, $"at {address} at {timeMs}ms");
        Assert.IsTrue(methods.TryFindMethodCodeStart(address, methodIndex, out var codeStart, out var tier), $"No code range of {expectedName} at {address}");
        Assert.AreEqual(expectedCodeStart, codeStart);
        Assert.AreEqual(expectedTier, tier);
    }

    private static void Load(UTraceManagedMethodList methods, long methodID, string methodName, UAddress address, ulong size, double loadTimeMs)
        => methods.GetOrCreateManagedMethod(1, 1, methodID, "dynamicClass", methodName, "()", 0, MethodFlags.Dynamic | MethodFlags.Jitted, address, new USize(size), UTimeSpan.FromMilliseconds(loadTimeMs));

    private static void Unload(UTraceManagedMethodList methods, long methodID, UAddress address, double unloadTimeMs)
        => methods.UnloadManagedMethod(methodID, address, UTimeSpan.FromMilliseconds(unloadTimeMs));

    private static void AssertMethodAt(UTraceManagedMethodList methods, UAddress address, double timeMs, string expectedName)
    {
        Assert.IsTrue(methods.TryFindMethodByAddress(address, UTimeSpan.FromMilliseconds(timeMs), out var method), $"No method found at {address} at {timeMs}ms");
        Assert.IsNotNull(method);
        Assert.AreEqual(expectedName, method.MethodName);
    }
}
