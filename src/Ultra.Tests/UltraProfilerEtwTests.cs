// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing.Parsers;
using Ultra.Core;

namespace Ultra.Tests;

[TestClass]
public class UltraProfilerEtwTests
{
    /// <summary>
    /// The CLR events are collected without a stack (neither the runtime stack walk of the Stack keyword nor an ETW stack): only
    /// the stacks of the CPU samples are converted, and a stack per CLR event slowed down the profiled app by up to 80% (#32).
    /// </summary>
    [TestMethod]
    public void ClrEventsAreCollectedWithoutStacks()
    {
        var keywords = UltraProfilerEtw.ClrKeywords;
        Assert.IsFalse(keywords.HasFlag(ClrTraceEventParser.Keywords.Stack), "the Stack keyword is not collected");
        Assert.IsFalse(UltraProfilerEtw.CreateClrProviderOptions().StacksEnabled);

        // What the conversion and the report still need: the managed symbols and the GC markers
        foreach (var keyword in new[] { ClrTraceEventParser.Keywords.JITSymbols, ClrTraceEventParser.Keywords.Loader, ClrTraceEventParser.Keywords.GC })
        {
            Assert.IsTrue(keywords.HasFlag(keyword), $"{keyword} is collected");
        }
    }

    /// <summary>
    /// The kernel session: the CPU samples always with their stacks; the hardware counter samples (--pmc) without a stack unless
    /// --pmc-stacks (Ivo 2026-11-10: the counters' stack walks were 80% of the stacks of a TOP trace and most of its conversion time;
    /// without them a counter sample is the code address where it fired).
    /// </summary>
    [TestMethod]
    public void CounterSamplesHaveAStackOnlyWithPmcStacks()
    {
        Assert.IsFalse(new UltraProfilerOptions().HardwareCounterStacks, "no counter stacks by default");

        var (events, stacks) = UltraProfilerEtw.KernelKeywords(hardwareCounters: false, counterStacks: false);
        Assert.IsTrue(stacks.HasFlag(KernelTraceEventParser.Keywords.Profile), "the CPU samples have their stacks");
        Assert.IsFalse(events.HasFlag(KernelTraceEventParser.Keywords.PMCProfile), "no counters without --pmc");

        (events, stacks) = UltraProfilerEtw.KernelKeywords(hardwareCounters: true, counterStacks: false);
        Assert.IsTrue(events.HasFlag(KernelTraceEventParser.Keywords.PMCProfile), "the counter samples");
        Assert.IsFalse(stacks.HasFlag(KernelTraceEventParser.Keywords.PMCProfile), "without their stacks");
        Assert.IsTrue(stacks.HasFlag(KernelTraceEventParser.Keywords.Profile), "the CPU samples keep theirs");

        (events, stacks) = UltraProfilerEtw.KernelKeywords(hardwareCounters: true, counterStacks: true);
        Assert.IsTrue(events.HasFlag(KernelTraceEventParser.Keywords.PMCProfile) && stacks.HasFlag(KernelTraceEventParser.Keywords.PMCProfile), "--pmc-stacks: the counter samples with their stacks");
    }
}
