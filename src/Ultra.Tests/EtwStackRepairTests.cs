// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core;
using ProfileThread = Ultra.Core.FirefoxProfiler.Thread;

namespace Ultra.Tests;

[TestClass]
public class EtwStackRepairTests
{
    [TestMethod]
    public void RepairsShortStackBetweenSamplesWithSharedPrefix()
    {
        var thread = new ProfileThread();

        var root = AddFrame(thread, "Root");
        var dispatch = AddFrame(thread, "Dispatch");
        var work = AddFrame(thread, "Work");
        var common = AddFrame(thread, "Common");
        var previousLeaf = AddFrame(thread, "PreviousLeaf");
        var nextLeaf = AddFrame(thread, "NextLeaf");
        var incompleteLeaf = AddFrame(thread, "IncompleteLeaf");

        var rootStack = AddStack(thread, root, null);
        var dispatchStack = AddStack(thread, dispatch, rootStack);
        var workStack = AddStack(thread, work, dispatchStack);
        var commonStack = AddStack(thread, common, workStack);
        var previousStack = AddStack(thread, previousLeaf, commonStack);
        var nextStack = AddStack(thread, nextLeaf, commonStack);
        var incompleteStack = AddStack(thread, incompleteLeaf, null);

        AddSample(thread, previousStack);
        AddSample(thread, incompleteStack);
        AddSample(thread, nextStack);

        var repairedCount = UltraConverterToFirefoxEtw.RepairIsolatedIncompleteStacks(thread);

        Assert.AreEqual(1, repairedCount);
        CollectionAssert.AreEqual(
            new[] { "Root", "Dispatch", "Work", "Common", "IncompleteLeaf" },
            GetStackNames(thread, thread.Samples.Stack[1]!.Value).ToArray());
    }

    [TestMethod]
    public void DiscardsShortBrokenNativeFragmentsBetweenSamplesWithSharedPrefix()
    {
        var thread = new ProfileThread();

        var root = AddFrame(thread, "Root");
        var dispatch = AddFrame(thread, "Dispatch");
        var work = AddFrame(thread, "Work");
        var caller1 = AddFrame(thread, "Caller1");
        var caller2 = AddFrame(thread, "Caller2");
        var caller3 = AddFrame(thread, "Caller3");
        var caller4 = AddFrame(thread, "Caller4");
        var caller5 = AddFrame(thread, "Caller5");
        var caller6 = AddFrame(thread, "Caller6");
        var caller7 = AddFrame(thread, "Caller7");
        var caller8 = AddFrame(thread, "Caller8");
        var caller9 = AddFrame(thread, "Caller9");
        var common = AddFrame(thread, "Common");
        var previousLeaf = AddFrame(thread, "PreviousLeaf");
        var nextLeaf = AddFrame(thread, "NextLeaf");
        var unknown = AddFrame(thread, "!0x00007FFB55D10031", UltraConverterToFirefox.CategoryNative);
        var kernel1 = AddFrame(thread, "ntoskrnl!0xFFFFF801E60B0D7A", UltraConverterToFirefox.CategoryKernel);
        var kernel2 = AddFrame(thread, "ntoskrnl!0xFFFFF801E60B1160", UltraConverterToFirefox.CategoryKernel);
        var kernel3 = AddFrame(thread, "ntoskrnl!0xFFFFF801E5DF8683", UltraConverterToFirefox.CategoryKernel);
        var kernel4 = AddFrame(thread, "ntoskrnl!0xFFFFF801E65BF2E6", UltraConverterToFirefox.CategoryKernel);
        var kernel5 = AddFrame(thread, "ntoskrnl!0xFFFFF801E5C12E9E", UltraConverterToFirefox.CategoryKernel);
        var kernel6 = AddFrame(thread, "ntoskrnl!0xFFFFF801E5C1650B", UltraConverterToFirefox.CategoryKernel);
        var kernel7 = AddFrame(thread, "ntoskrnl!0xFFFFF801E5D62E5C", UltraConverterToFirefox.CategoryKernel);
        var kernel8 = AddFrame(thread, "ntoskrnl!0xFFFFF801E5D6279A", UltraConverterToFirefox.CategoryKernel);
        var kernel9 = AddFrame(thread, "ntoskrnl!0xFFFFF801E5D6199B", UltraConverterToFirefox.CategoryKernel);
        var kernel10 = AddFrame(thread, "ntoskrnl!0xFFFFF801E5D61D29", UltraConverterToFirefox.CategoryKernel);

        var rootStack = AddStack(thread, root, null);
        var dispatchStack = AddStack(thread, dispatch, rootStack);
        var workStack = AddStack(thread, work, dispatchStack);
        var caller1Stack = AddStack(thread, caller1, workStack);
        var caller2Stack = AddStack(thread, caller2, caller1Stack);
        var caller3Stack = AddStack(thread, caller3, caller2Stack);
        var caller4Stack = AddStack(thread, caller4, caller3Stack);
        var caller5Stack = AddStack(thread, caller5, caller4Stack);
        var caller6Stack = AddStack(thread, caller6, caller5Stack);
        var caller7Stack = AddStack(thread, caller7, caller6Stack);
        var caller8Stack = AddStack(thread, caller8, caller7Stack);
        var caller9Stack = AddStack(thread, caller9, caller8Stack);
        var commonStack = AddStack(thread, common, caller9Stack);
        var previousStack = AddStack(thread, previousLeaf, commonStack);
        var nextStack = AddStack(thread, nextLeaf, commonStack);
        var unknownStack = AddStack(thread, unknown, null);
        var kernel1Stack = AddStack(thread, kernel1, unknownStack);
        var kernel2Stack = AddStack(thread, kernel2, kernel1Stack);
        var kernel3Stack = AddStack(thread, kernel3, kernel2Stack);
        var kernel4Stack = AddStack(thread, kernel4, kernel3Stack);
        var kernel5Stack = AddStack(thread, kernel5, kernel4Stack);
        var kernel6Stack = AddStack(thread, kernel6, kernel5Stack);
        var kernel7Stack = AddStack(thread, kernel7, kernel6Stack);
        var kernel8Stack = AddStack(thread, kernel8, kernel7Stack);
        var kernel9Stack = AddStack(thread, kernel9, kernel8Stack);
        var kernel10Stack = AddStack(thread, kernel10, kernel9Stack);

        AddSample(thread, previousStack);
        AddSample(thread, kernel10Stack);
        AddSample(thread, unknownStack);
        AddSample(thread, nextStack);

        var repairedCount = UltraConverterToFirefoxEtw.RepairIsolatedIncompleteStacks(thread);

        Assert.AreEqual(2, repairedCount);
        Assert.AreEqual(commonStack, thread.Samples.Stack[1]);
        Assert.AreEqual(commonStack, thread.Samples.Stack[2]);
    }

    [TestMethod]
    public void KeepsResolvedNativeFrameWhenRepairingShortStack()
    {
        var thread = new ProfileThread();

        var root = AddFrame(thread, "Root");
        var dispatch = AddFrame(thread, "Dispatch");
        var work = AddFrame(thread, "Work");
        var common = AddFrame(thread, "Common");
        var previousLeaf = AddFrame(thread, "PreviousLeaf");
        var nextLeaf = AddFrame(thread, "NextLeaf");
        var ntTraceEvent = AddFrame(thread, "NtTraceEvent", UltraConverterToFirefox.CategoryNative);

        var rootStack = AddStack(thread, root, null);
        var dispatchStack = AddStack(thread, dispatch, rootStack);
        var workStack = AddStack(thread, work, dispatchStack);
        var commonStack = AddStack(thread, common, workStack);
        var previousStack = AddStack(thread, previousLeaf, commonStack);
        var nextStack = AddStack(thread, nextLeaf, commonStack);
        var ntTraceEventStack = AddStack(thread, ntTraceEvent, null);

        AddSample(thread, previousStack);
        AddSample(thread, ntTraceEventStack);
        AddSample(thread, nextStack);

        var repairedCount = UltraConverterToFirefoxEtw.RepairIsolatedIncompleteStacks(thread);

        Assert.AreEqual(1, repairedCount);
        CollectionAssert.AreEqual(
            new[] { "Root", "Dispatch", "Work", "Common", "NtTraceEvent" },
            GetStackNames(thread, thread.Samples.Stack[1]!.Value).ToArray());
    }

    [TestMethod]
    public void ReusesExistingStackWhenRepairTargetAlreadyExists()
    {
        var thread = new ProfileThread();

        var root = AddFrame(thread, "Root");
        var dispatch = AddFrame(thread, "Dispatch");
        var work = AddFrame(thread, "Work");
        var common = AddFrame(thread, "Common");
        var incompleteLeaf = AddFrame(thread, "IncompleteLeaf");
        var nextLeaf = AddFrame(thread, "NextLeaf");

        var rootStack = AddStack(thread, root, null);
        var dispatchStack = AddStack(thread, dispatch, rootStack);
        var workStack = AddStack(thread, work, dispatchStack);
        var commonStack = AddStack(thread, common, workStack);
        var existingIncompleteStack = AddStack(thread, incompleteLeaf, commonStack);
        var nextStack = AddStack(thread, nextLeaf, existingIncompleteStack);
        var incompleteStack = AddStack(thread, incompleteLeaf, null);
        var originalStackTableLength = thread.StackTable.Length;

        AddSample(thread, existingIncompleteStack);
        AddSample(thread, incompleteStack);
        AddSample(thread, nextStack);

        var repairedCount = UltraConverterToFirefoxEtw.RepairIsolatedIncompleteStacks(thread);

        Assert.AreEqual(1, repairedCount);
        Assert.AreEqual(existingIncompleteStack, thread.Samples.Stack[1]);
        Assert.AreEqual(originalStackTableLength, thread.StackTable.Length);
    }

    [TestMethod]
    public void DoesNotRepairWhenSurroundingSamplesDoNotShareEnoughPrefix()
    {
        var thread = new ProfileThread();

        var root = AddFrame(thread, "Root");
        var previous = AddFrame(thread, "Previous");
        var next = AddFrame(thread, "Next");
        var incompleteLeaf = AddFrame(thread, "IncompleteLeaf");

        var rootStack = AddStack(thread, root, null);
        var previousStack = AddStack(thread, previous, rootStack);
        var nextStack = AddStack(thread, next, rootStack);
        var incompleteStack = AddStack(thread, incompleteLeaf, null);

        AddSample(thread, previousStack);
        AddSample(thread, incompleteStack);
        AddSample(thread, nextStack);

        var repairedCount = UltraConverterToFirefoxEtw.RepairIsolatedIncompleteStacks(thread);

        Assert.AreEqual(0, repairedCount);
        Assert.AreEqual(incompleteStack, thread.Samples.Stack[1]);
    }

    [TestMethod]
    public void DoesNotRepairStackThatIsAlreadyPrefixOfSurroundingSamples()
    {
        var thread = new ProfileThread();

        var root = AddFrame(thread, "Root");
        var dispatch = AddFrame(thread, "Dispatch");
        var work = AddFrame(thread, "Work");
        var common = AddFrame(thread, "Common");
        var previousLeaf = AddFrame(thread, "PreviousLeaf");
        var nextLeaf = AddFrame(thread, "NextLeaf");

        var rootStack = AddStack(thread, root, null);
        var dispatchStack = AddStack(thread, dispatch, rootStack);
        var workStack = AddStack(thread, work, dispatchStack);
        var commonStack = AddStack(thread, common, workStack);
        var previousStack = AddStack(thread, previousLeaf, commonStack);
        var nextStack = AddStack(thread, nextLeaf, commonStack);

        AddSample(thread, previousStack);
        AddSample(thread, dispatchStack);
        AddSample(thread, nextStack);

        var repairedCount = UltraConverterToFirefoxEtw.RepairIsolatedIncompleteStacks(thread);

        Assert.AreEqual(0, repairedCount);
        Assert.AreEqual(dispatchStack, thread.Samples.Stack[1]);
    }

    private static int AddFrame(ProfileThread thread, string name, int category = UltraConverterToFirefox.CategoryManaged)
    {
        var stringIndex = thread.StringArray.Count;
        thread.StringArray.Add(name);

        var funcIndex = thread.FuncTable.Length;
        thread.FuncTable.Name.Add(stringIndex);
        thread.FuncTable.IsJS.Add(false);
        thread.FuncTable.RelevantForJS.Add(false);
        thread.FuncTable.Resource.Add(-1);
        thread.FuncTable.FileName.Add(null);
        thread.FuncTable.LineNumber.Add(null);
        thread.FuncTable.ColumnNumber.Add(null);
        thread.FuncTable.Length++;

        var frameIndex = thread.FrameTable.Length;
        thread.FrameTable.Address.Add(0);
        thread.FrameTable.InlineDepth.Add(0);
        thread.FrameTable.Category.Add(category);
        thread.FrameTable.Subcategory.Add(0);
        thread.FrameTable.Func.Add(funcIndex);
        thread.FrameTable.NativeSymbol.Add(null);
        thread.FrameTable.InnerWindowID.Add(null);
        thread.FrameTable.Implementation.Add(null);
        thread.FrameTable.Line.Add(null);
        thread.FrameTable.Column.Add(null);
        thread.FrameTable.Length++;
        return frameIndex;
    }

    private static int AddStack(ProfileThread thread, int frameIndex, int? prefix)
    {
        var stackIndex = thread.StackTable.Length;
        thread.StackTable.Frame.Add(frameIndex);
        thread.StackTable.Category.Add(thread.FrameTable.Category[frameIndex]);
        thread.StackTable.Subcategory.Add(0);
        thread.StackTable.Prefix.Add(prefix);
        thread.StackTable.Length++;
        return stackIndex;
    }

    private static void AddSample(ProfileThread thread, int stackIndex)
    {
        thread.Samples.Stack.Add(stackIndex);
        thread.Samples.Length++;
    }

    private static List<string> GetStackNames(ProfileThread thread, int stackIndex)
    {
        var stackNames = new List<string>();
        var currentStackIndex = stackIndex;
        while (currentStackIndex >= 0)
        {
            var frameIndex = thread.StackTable.Frame[currentStackIndex];
            var funcIndex = thread.FrameTable.Func[frameIndex];
            var stringIndex = thread.FuncTable.Name[funcIndex];
            stackNames.Add(thread.StringArray[stringIndex]);
            currentStackIndex = thread.StackTable.Prefix[currentStackIndex] ?? -1;
        }

        stackNames.Reverse();
        return stackNames;
    }
}
