// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Ultra.Core;
using Ultra.Core.Markers;
using Ultra.Core.Report;

namespace Ultra.Tests;

[TestClass]
public class UltraProfileReportTests
{
    /// <summary>
    /// A profile of two CPU threads and one counter track, written with the profile model and read back by the report: the CPU, the
    /// shares, the leaf functions, the code offsets and the counter events; the counter track is not counted as CPU. The numbers do
    /// not depend on the culture of the machine (a regression: <c>duration=2,0 s</c> with a French region).
    /// </summary>
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public void ReportSumsCpuThreadsApartFromCounterTracks(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            AssertReportSumsCpuThreadsApartFromCounterTracks();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static void AssertReportSumsCpuThreadsApartFromCounterTracks()
    {
        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.Abi = "linux-x64";
        profile.Meta.LogicalCPUs = 4;
        profile.Meta.ProfilingEndTime = 2000;
        profile.Meta.Interval = 0.5;
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

        StringAssert.Contains(text, "product=Test abi=linux-x64 cpus=4 duration=2.0 s sampling=2000/s");
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
        StringAssert.Contains(text, "GC: no GC or allocation events in the profile");
    }

    /// <summary>
    /// The GC markers of a worker (allocation ticks) and of a GC thread (suspensions, a blocking gen0 and a background gen2
    /// collection, a restart without payload, heap stats): the collections, the pauses from a suspension start to the restart end
    /// after it, the heap and the allocations per thread, type and kind, whatever the culture.
    /// </summary>
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public void ReportSumsGcAndAllocations(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            const long MiB = 1024 * 1024;
            var profile = new FirefoxProfiler.Profile();
            profile.Meta.Product = "Test";
            profile.Meta.ProfilingEndTime = 2000;
            profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];

            var worker = MakeThread("0 - worker (10)", "10", samples: [("Inner", 0, 1_000_000)], weights: null);
            for (int i = 0; i < 3; i++) AddMarker(worker, "0 - GC Alloc (10)", 5 + i, 5 + i, new GCAllocationTickEvent { AllocationAmount = MiB, AllocationKind = "Small", TypeName = "System.Byte[]" });
            AddMarker(worker, "0 - GC Alloc (10)", 9, 9, new GCAllocationTickEvent { AllocationAmount = MiB, AllocationKind = "Small", TypeName = "System.String" });
            AddMarker(worker, "0 - GC Alloc (10)", 9.5, 9.5, new GCAllocationTickEvent { AllocationAmount = 5 * MiB, AllocationKind = "Large", TypeName = "System.Double[]" });
            profile.Threads.Add(worker);

            var gcThread = MakeThread("1 - gc (11)", "11", samples: [("Outer", 0, 1_000_000)], weights: null);
            AddMarker(gcThread, "GC Suspend EE", 10, 10.5, new GCSuspendExecutionEngineEvent { Reason = "SuspendForGC", Count = 1 });
            AddMarker(gcThread, "GC Event", 10.5, 12.5, new GCEvent { Reason = "AllocSmall", Count = 1, Depth = 0, GCType = "NonConcurrentGC" });
            AddMarker(gcThread, "GC Restart EE", 12.5, 13, null);
            AddMarker(gcThread, "GCHeapStats", 13, 13, new GCHeapStatsEvent { TotalHeapSize = 10 * MiB, GenerationSize0 = MiB, GenerationSize1 = 2 * MiB, GenerationSize2 = 5 * MiB, GenerationSize3 = 2 * MiB });
            AddMarker(gcThread, "GC Suspend EE", 100, 100.2, new GCSuspendExecutionEngineEvent { Reason = "SuspendForGCPrep", Count = 2 });
            AddMarker(gcThread, "GC Event", 100.2, 150, new GCEvent { Reason = "Induced", Count = 2, Depth = 2, GCType = "BackgroundGC" });
            AddMarker(gcThread, "GC Restart EE", 100.4, 100.6, null);
            AddMarker(gcThread, "GCHeapStats", 150, 150, new GCHeapStatsEvent { TotalHeapSize = 8 * MiB });
            // a suspension for something else than a GC (e.g. tiered compilation): reported apart
            AddMarker(gcThread, "GC Suspend EE", 200, 200.1, new GCSuspendExecutionEngineEvent { Reason = "SuspendOther", Count = 3 });
            AddMarker(gcThread, "GC Restart EE", 200.1, 200.3, null);
            profile.Threads.Add(gcThread);

            var json = JsonSerializer.SerializeToUtf8Bytes(profile, FirefoxProfiler.JsonProfilerContext.Default.Profile);
            var output = new StringWriter();
            new UltraProfileReport(top: 5, workers: 2).Run(new MemoryStream(json), output);
            var text = output.ToString();

            StringAssert.Contains(text, "GC: 2 collections (gen0 1, gen1 0, gen2 1, 1 of them background)");
            StringAssert.Contains(text, "  reasons: AllocSmall 1, Induced 1");
            StringAssert.Contains(text, "  GC pauses (managed threads stopped): 2, 3.6 ms = 0.18% of the 2.0 s, longest 3.00 ms, of which 0.7 ms suspending the threads (longest 0.50 ms)");
            StringAssert.Contains(text, "  other runtime pauses (not for a GC: tiered compilation, debugger...): 1, 0.3 ms, longest 0.30 ms");
            StringAssert.Contains(text, "  in blocking collections: 2.0 ms (longest 2.00 ms); the background collections ran 49.8 ms beside the program");
            StringAssert.Contains(text, "  GC heap: largest 10.0 MB after a collection (gen0 1.0, gen1 2.0, gen2 5.0, LOH 2.0, POH 0.0 MB), 8.0 MB after the last one");
            StringAssert.Contains(text, "allocations: about 9.0 MB = 4.5 MB/s, from 5 allocation ticks (Large 5.0 MB, Small 4.0 MB)");
            StringAssert.Contains(text, "0 - worker (10)                                     9.0 MB 100.0%         5");
            StringAssert.Contains(text, "5.0 MB  55.6%         1  System.Double[]");
            StringAssert.Contains(text, "3.0 MB  33.3%         3  System.Byte[]");
            StringAssert.Contains(text, "1.0 MB  11.1%         1  System.String");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>
    /// Lock waits (--locks) on two threads: the totals, the time per waiting thread, the waiting code (from the stack of the wait, the
    /// first .NET frame outside the lock code: not the native frames of the event, not Monitor), the lock owners by thread name, and
    /// a wait without a stack (Linux).
    /// </summary>
    [TestMethod]
    public void ReportSumsLockContention()
    {
        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.ProfilingEndTime = 2000;
        profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];

        var worker = MakeThread("0 - Worker (10)", "10", samples: [("Inner", 0, 1_000_000)], weights: null);
        // the stack of a wait, root first: App.Work (.NET) -> TaskAwaiter (.NET, GetAwaiter().GetResult()) -> Task.InternalWait in IL form (.NET)
        // -> System.Threading.Monitor.Enter (.NET) -> NtTraceEvent (native)
        var waitStack = AddStack(worker, ("App.Work()", UltraConverterToFirefox.CategoryManaged), ("System.Void System.Runtime.CompilerServices.TaskAwaiter::HandleNonSuccessAndDebuggerNotification(System.Threading.Tasks.Task,System.Threading.Tasks.ConfigureAwaitOptions)", UltraConverterToFirefox.CategoryManaged),
            ("System.Boolean System.Threading.Tasks.Task::InternalWait(System.Int32,System.Threading.CancellationToken)", UltraConverterToFirefox.CategoryManaged),
            ("System.Threading.Monitor.Enter(class System.Object)", UltraConverterToFirefox.CategoryManaged), ("NtTraceEvent", UltraConverterToFirefox.CategoryNative));
        AddMarker(worker, LockContentionEvent.MarkerName, 100, 110, new LockContentionEvent { DurationMs = 10, Kind = "Managed", LockId = 0x1000, OwnerThreadId = 11, CauseStack = waitStack });
        AddMarker(worker, LockContentionEvent.MarkerName, 200, 230, new LockContentionEvent { DurationMs = 30, Kind = "Managed", LockId = 0x1000, OwnerThreadId = 11, CauseStack = waitStack });
        profile.Threads.Add(worker);

        var other = MakeThread("1 - Holder (11)", "11", samples: [("Outer", 0, 1_000_000)], weights: null);
        AddMarker(other, LockContentionEvent.MarkerName, 300, 310, new LockContentionEvent { DurationMs = 10, Kind = "Managed", LockId = 0x2000, OwnerThreadId = 10 });
        profile.Threads.Add(other);

        var json = JsonSerializer.SerializeToUtf8Bytes(profile, FirefoxProfiler.JsonProfilerContext.Default.Profile);
        var output = new StringWriter();
        new UltraProfileReport(top: 5, workers: 2).Run(new MemoryStream(json), output);
        var text = output.ToString();

        StringAssert.Contains(text, "lock contention: 3 waits, 50.0 ms waiting in total (longest 30.00 ms), on 2 locks");
        StringAssert.Contains(text, "  0 - Worker (10)                                    40.0  80.0%        2      30.00");
        StringAssert.Contains(text, "  1 - Holder (11)                                    10.0  20.0%        1      10.00");
        StringAssert.Contains(text, "        40.0  80.0%        2  App.Work()");
        StringAssert.Contains(text, "        10.0  20.0%        1  (no stack: not recorded on Linux and macOS)");
        StringAssert.Contains(text, "        40.0  80.0%        2  1 - Holder (11)");
        StringAssert.Contains(text, "        10.0  20.0%        1  0 - Worker (10)");
    }

    /// <summary>Adds a stack (root first) of new frames with their categories to a thread, returns the index of its leaf.</summary>
    private static int AddStack(FirefoxProfiler.Thread thread, params (string Name, int Category)[] rootToLeaf)
    {
        int? prefix = null;
        foreach (var (name, category) in rootToLeaf)
        {
            thread.StringArray.Add(name);
            var func = thread.FuncTable.Length;
            thread.FuncTable.Name.Add(thread.StringArray.Count - 1);
            thread.FuncTable.IsJS.Add(false);
            thread.FuncTable.RelevantForJS.Add(false);
            thread.FuncTable.Resource.Add(-1);
            thread.FuncTable.FileName.Add(null);
            thread.FuncTable.LineNumber.Add(null);
            thread.FuncTable.ColumnNumber.Add(null);
            thread.FuncTable.Length++;
            var frame = thread.FrameTable.Length;
            thread.FrameTable.Func.Add(func);
            thread.FrameTable.Address.Add(-1);
            thread.FrameTable.InlineDepth.Add(0);
            thread.FrameTable.Category.Add(category);
            thread.FrameTable.Subcategory.Add(0);
            thread.FrameTable.NativeSymbol.Add(null);
            thread.FrameTable.InnerWindowID.Add(null);
            thread.FrameTable.Implementation.Add(null);
            thread.FrameTable.Line.Add(null);
            thread.FrameTable.Column.Add(null);
            thread.FrameTable.Length++;
            var stack = thread.StackTable.Length;
            thread.StackTable.Frame.Add(frame);
            thread.StackTable.Prefix.Add(prefix);
            thread.StackTable.Category.Add(category);
            thread.StackTable.Subcategory.Add(0);
            thread.StackTable.Length++;
            prefix = stack;
        }
        return prefix!.Value;
    }

    private static void AddMarker(FirefoxProfiler.Thread thread, string name, double start, double end, FirefoxProfiler.MarkerPayload? payload)
    {
        var markers = thread.Markers;
        thread.StringArray.Add(name);
        markers.Name.Add(thread.StringArray.Count - 1);
        markers.StartTime.Add(start);
        markers.EndTime.Add(end);
        markers.Phase.Add(start == end ? FirefoxProfiler.MarkerPhase.Instance : FirefoxProfiler.MarkerPhase.Interval);
        markers.Category.Add(0);
        markers.ThreadId.Add(0);
        markers.Data.Add(payload);
        markers.Length++;
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
        if (weights != null) thread.Samples.Weight = new List<long>();
        else thread.Samples.ThreadCPUDelta = new List<int?>();
        for (int i = 0; i < samples.Length; i++)
        {
            var (leaf, offset, cpuNs) = samples[i];
            thread.Samples.Stack.Add(Stack(Frame(leaf, offset), root));
            thread.Samples.TimeDeltas.Add(1);
            if (weights != null) thread.Samples.Weight!.Add(weights[i]);
            else thread.Samples.ThreadCPUDelta!.Add((int)cpuNs);
            thread.Samples.Length++;
        }
        return thread;
    }
}
