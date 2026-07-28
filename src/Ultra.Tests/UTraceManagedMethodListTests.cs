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
