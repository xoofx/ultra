// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text;
using System.Text.Json;
using Ultra.Core;
using Ultra.Core.Report;

namespace Ultra.Tests;

[TestClass]
public class UltraProfileReportTests
{
    /// <summary>
    /// A profile of two CPU threads and one counter track, written with the profile model and read back by the report: the CPU, the
    /// shares, the leaf functions, the code offsets and the counter events; the counter track is not counted as CPU.
    /// </summary>
    [TestMethod]
    public void ReportSumsCpuThreadsApartFromCounterTracks()
    {
        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.Abi = "linux-x64";
        profile.Meta.LogicalCPUs = 4;
        profile.Meta.ProfilingEndTime = 2000;
        profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];

        // thread A: 3 samples of Inner (offsets 0, 0, 8) under Outer, 1 ms CPU each; thread B: 1 sample of Outer
        profile.Threads.Add(MakeThread("0 - worker (10)", "10", samples: [("Inner", 0, 1_000_000), ("Inner", 0, 1_000_000), ("Inner", 8, 1_000_000)], weights: null));
        profile.Threads.Add(MakeThread("1 - main (9)", "9", samples: [("Outer", 4, 1_000_000)], weights: null));
        // the counter track of thread A: 2 samples standing for 100 and 300 misses
        profile.Threads.Add(MakeThread("0 - worker (10) [cache-misses]", "10.1", samples: [("Inner", 0, 0), ("Outer", 4, 0)], weights: [100, 300]));

        var json = JsonSerializer.SerializeToUtf8Bytes(profile, FirefoxProfiler.JsonProfilerContext.Default.Profile);
        var output = new StringWriter();
        new UltraProfileReport(top: 5, codeOffsets: true, workers: 2).Run(new MemoryStream(json), output);
        var text = output.ToString();

        StringAssert.Contains(text, "product=Test abi=linux-x64 cpus=4 duration=2.0 s");
        StringAssert.Contains(text, "3 threads in the file: 2 CPU threads and 1 counter tracks (cache-misses: 1)");
        StringAssert.Contains(text, "CPU: 0.0 s over 2.0 s = 0.00 cores busy on average, 4 samples");
        StringAssert.Contains(text, "0 - worker (10)                                      3          3 ms  75.0%");
        StringAssert.Contains(text, "1 - main (9)                                         1          1 ms  25.0%");
        StringAssert.Contains(text, "3  75.0%  Inner");
        StringAssert.Contains(text, "1  25.0%  Outer");
        StringAssert.Contains(text, "hot code offsets in Inner (3 samples with an address, 2 distinct):");
        StringAssert.Contains(text, "+0x0: 2 (66.7%)");
        StringAssert.Contains(text, "+0x8: 1 (33.3%)");
        StringAssert.Contains(text, "counter [cache-misses]: 1 tracks, 2 samples standing for 400 events");
        StringAssert.Contains(text, "300  75.0%  Outer");
        StringAssert.Contains(text, "100  25.0%  Inner");
    }

    /// <summary>A thread whose samples are leaf-first stacks of (leaf function, code offset) under a root "Main" frame.</summary>
    private static FirefoxProfiler.Thread MakeThread(string name, string tid, (string Leaf, int Offset, long CpuNs)[] samples, long[]? weights)
    {
        var thread = new FirefoxProfiler.Thread { Name = name, ProcessName = "Test (10)", Pid = "10", Tid = tid, ProcessType = "default" };
        var strings = new Dictionary<string, int>();
        int String(string s)
        {
            if (!strings.TryGetValue(s, out var i))
            {
                i = thread.StringArray.Count;
                thread.StringArray.Add(s);
                strings[s] = i;
            }
            return i;
        }
        var funcs = new Dictionary<string, int>();
        int Func(string f)
        {
            if (!funcs.TryGetValue(f, out var i))
            {
                i = thread.FuncTable.Length;
                thread.FuncTable.Name.Add(String(f));
                thread.FuncTable.IsJS.Add(false);
                thread.FuncTable.RelevantForJS.Add(false);
                thread.FuncTable.Resource.Add(-1);
                thread.FuncTable.FileName.Add(null);
                thread.FuncTable.LineNumber.Add(null);
                thread.FuncTable.ColumnNumber.Add(null);
                thread.FuncTable.Length++;
            }
            return i;
        }
        int Frame(string f, int address)
        {
            var i = thread.FrameTable.Length;
            thread.FrameTable.Func.Add(Func(f));
            thread.FrameTable.Address.Add(address);
            thread.FrameTable.InlineDepth.Add(0);
            thread.FrameTable.Category.Add(0);
            thread.FrameTable.Subcategory.Add(0);
            thread.FrameTable.NativeSymbol.Add(null);
            thread.FrameTable.InnerWindowID.Add(null);
            thread.FrameTable.Implementation.Add(null);
            thread.FrameTable.Line.Add(null);
            thread.FrameTable.Column.Add(null);
            thread.FrameTable.Length++;
            return i;
        }
        int Stack(int frame, int? prefix)
        {
            var i = thread.StackTable.Length;
            thread.StackTable.Frame.Add(frame);
            thread.StackTable.Prefix.Add(prefix);
            thread.StackTable.Category.Add(0);
            thread.StackTable.Subcategory.Add(0);
            thread.StackTable.Length++;
            return i;
        }

        var root = Stack(Frame("Main", 0), null);
        thread.Samples.TimeDeltas = new List<double>();
        thread.Samples.WeightType = "samples";
        if (weights != null) thread.Samples.Weight = new List<int>();
        else thread.Samples.ThreadCPUDelta = new List<int?>();
        for (int i = 0; i < samples.Length; i++)
        {
            var (leaf, offset, cpuNs) = samples[i];
            thread.Samples.Stack.Add(Stack(Frame(leaf, offset), root));
            thread.Samples.TimeDeltas.Add(1);
            if (weights != null) thread.Samples.Weight!.Add((int)weights[i]);
            else thread.Samples.ThreadCPUDelta!.Add((int)cpuNs);
            thread.Samples.Length++;
        }
        return thread;
    }
}
