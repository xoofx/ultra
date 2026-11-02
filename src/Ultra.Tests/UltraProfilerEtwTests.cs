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
}
