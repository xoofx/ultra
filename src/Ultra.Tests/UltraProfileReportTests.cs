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

    /// <summary>
    /// A lock wait whose event names no holder (owner 0) is a holder row of its own, so the holder rows add up to the total wait, in
    /// both texts; when no event names a holder (the runtime before .NET 8) there is no holder list at all.
    /// </summary>
    [TestMethod]
    public void LockWaitsWithoutAHolderAreAHolderRow()
    {
        static FirefoxProfiler.Profile Waits(long firstOwner)
        {
            var profile = new FirefoxProfiler.Profile();
            profile.Meta.Product = "Test";
            profile.Meta.ProfilingEndTime = 2000;
            profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];
            var worker = MakeThread("0 - Worker (10)", "10", samples: [("Inner", 0, 1_000_000)], weights: null);
            AddMarker(worker, LockContentionEvent.MarkerName, 100, 140, new LockContentionEvent { DurationMs = 40, Kind = "Managed", LockId = 0x1000, OwnerThreadId = firstOwner });
            AddMarker(worker, LockContentionEvent.MarkerName, 200, 220, new LockContentionEvent { DurationMs = 20, Kind = "Managed", LockId = 0x1000, OwnerThreadId = 0 });
            profile.Threads.Add(worker);
            profile.Threads.Add(MakeThread("1 - Holder (11)", "11", samples: [("Outer", 0, 1_000_000)], weights: null));
            return profile;
        }

        var compact = Report(Waits(11), new UltraProfileReportOptions { Compact = true, Workers = 2 });
        StringAssert.Contains(compact, "ms pct waits held_by\n40.0 66.7% 1 1 - Holder (11)\n20.0 33.3% 1 (no holder: the event names none)\nomitted: none\n");
        var plain = Report(Waits(11), new UltraProfileReportOptions { Workers = 2 });
        StringAssert.Contains(plain, "held by (ms waited on the locks the thread held, share, waits):\n        40.0  66.7%        1  1 - Holder (11)\n        20.0  33.3%        1  (no holder: the event names none)\n");

        var unnamed = Report(Waits(0), new UltraProfileReportOptions { Compact = true, Workers = 2 });
        StringAssert.Contains(unnamed, "total: 2 waits, 60.0ms waiting (max 40.00ms), 1 lock;");
        Assert.IsFalse(unnamed.Contains("held_by", StringComparison.Ordinal), "no holder named: no holder list");
        Assert.IsFalse(Report(Waits(0), new UltraProfileReportOptions { Workers = 2 }).Contains("held by", StringComparison.Ordinal), "no holder named: no holder list");
    }

    /// <summary>
    /// --thread: only the threads whose name matches are listed and counted, in both texts - no 0-sample rows for the others - and the
    /// header says the counts are the filter's.
    /// </summary>
    [TestMethod]
    public void ThreadFilterListsAndCountsOnlyTheMatchingThreads()
    {
        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.ProfilingEndTime = 2000;
        profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];
        profile.Threads.Add(MakeThread("0 - worker (10)", "10", samples: [("App.Work", 0, 1_000_000), ("App.Work", 0, 1_000_000)], weights: null));
        profile.Threads.Add(MakeThread("1 - main (9)", "9", samples: [("App.Run", 0, 1_000_000), ("App.Run", 0, 1_000_000), ("App.Run", 0, 1_000_000)], weights: null));
        profile.Threads.Add(MakeThread("2 - idle worker (8)", "8", samples: [("App.Wait", 0, 1_000_000)], weights: null));

        var compact = Report(profile, new UltraProfileReportOptions { Compact = true, ThreadFilter = "worker", Workers = 2 });
        StringAssert.Contains(compact, "thread filter: names containing \"worker\" (GC lines: whole process)\ntracks: 3 in the file; in the filter: 2 cpu threads, 0 counter tracks\n");
        StringAssert.Contains(compact, "## cpu by thread\ntotal: 0.0s cpu = 0.00 cores avg over 2.0s, 3 samples, 2 threads; pct of total cpu\ncpu_ms pct samples thread\n2 66.7% 2 0 - worker (10)\n1 33.3% 1 2 - idle worker (8)\nomitted: none\n");
        Assert.IsFalse(compact.Contains("main", StringComparison.Ordinal), "a thread outside the filter has no row");

        var plain = Report(profile, new UltraProfileReportOptions { ThreadFilter = "worker", Workers = 2 });
        StringAssert.Contains(plain, "3 threads in the file, those whose name contains \"worker\": 2 CPU threads and 0 counter tracks\n");
        Assert.IsFalse(plain.Contains("main", StringComparison.Ordinal), "a thread outside the filter has no row");
        Assert.IsFalse(plain.Contains("more threads", StringComparison.Ordinal), "no rest of 0-sample threads");
    }

    /// <summary>
    /// The compact text: short headers, the units in the column names, unpadded rows, the total and omitted lines of each list, and
    /// the function names without their parameters unless an overload needs them; whatever the culture.
    /// </summary>
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public void CompactReportListsTotalsAndOmitted(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var profile = new FirefoxProfiler.Profile();
            profile.Meta.Product = "Test";
            profile.Meta.ProfilingEndTime = 2000;
            profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];
            profile.Threads.Add(MakeThread("0 - worker (10)", "10", samples: [("App.Work(int32)", 0, 1_000_000), ("App.Work(int32)", 0, 1_000_000), ("App.Work(int32)", 0, 1_000_000), ("App.Run(int32)", 0, 1_000_000)], weights: null));
            profile.Threads.Add(MakeThread("1 - main (9)", "9", samples: [("App.Run(class System.String)", 0, 1_000_000), ("App.Run(class System.String)", 0, 1_000_000)], weights: null));
            profile.Threads.Add(MakeThread("2 - idle (8)", "8", samples: [("App.Work(int32)", 0, 1_000_000)], weights: null));

            var text = Report(profile, new UltraProfileReportOptions { Top = 2, Compact = true, Workers = 2 });

            StringAssert.Contains(text, "## profile\nproduct=Test abi= cpus= duration=2.0s\n");
            StringAssert.Contains(text, "## cpu by thread\ntotal: 0.0s cpu = 0.00 cores avg over 2.0s, 7 samples, 3 threads; pct of total cpu\ncpu_ms pct samples thread\n4 57.1% 4 0 - worker (10)\n2 28.6% 2 1 - main (9)\nomitted: 1 thread, 1ms = 14.3%\n");
            // App.Work has no overload: without its parameters; the two App.Run keep theirs
            StringAssert.Contains(text, "## self samples by function\ntotal: 7 samples over the cpu threads, 3 functions; pct of total samples\nsamples pct function\n4 57.1% App.Work\n2 28.6% App.Run(class System.String)\nomitted: 1 function, 1 sample = 14.3%\n");
            StringAssert.Contains(text, "## gc\nnone: no GC or allocation events");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>
    /// The call trees of --detailed: the top-down tree of the busiest thread (a chain of frames passing all their samples is one row,
    /// the rows under the min percent summed under their parent and in the omitted line), the callers of the top function over all
    /// the threads, and the callers and callees of a focus.
    /// </summary>
    [TestMethod]
    public void DetailedReportPrintsTrees()
    {
        var profile = TreeProfile();

        var text = Report(profile, new UltraProfileReportOptions { Compact = true, Detailed = true, TreeThreads = 1, Hotspots = 1, MinPercent = 6, Workers = 2 });

        StringAssert.Contains(text, "## top-down 0 - main (1)\ntotal: 100 samples, 100ms cpu; pct of the thread's samples\nincl pct self self_pct frames\n" +
            "100 100.0% 0 0.0% Main > Run\n" +
            " 70 70.0% 10 10.0% Compute\n" +
            "  60 60.0% 60 60.0% Inner\n" +
            " 20 20.0% 20 20.0% Load > Inner\n" +
            " 10 10.0% [+2 more callees]\n" +
            "omitted: 10 samples = 10.0%: 10 under 6% (2 branches)\n");
        Assert.IsFalse(text.Contains("## top-down 1 - side (2)"), "only the busiest thread");

        // Inner is the top function by self samples: 60 + 20 in main, 30 in side
        StringAssert.Contains(text, "## callers Inner\ntotal: 110 samples with it on the stack = 78.6% of 140 cpu samples, self 110 = 78.6%, all threads; pct of 110; self = it on top\nincl pct self self_pct frames\n" +
            "110 100.0% 110 100.0% Inner\n" +
            " 60 54.5% 60 54.5% Compute < Run < Main\n" +
            " 30 27.3% 30 27.3% Side\n" +
            " 20 18.2% 20 18.2% Load < Run < Main\n" +
            "omitted: none\n");

        var focus = Report(profile, new UltraProfileReportOptions { Compact = true, Focus = "run", TreeThreads = 0, MinPercent = 6, Workers = 2 });
        StringAssert.Contains(focus, "## callers run\ntotal: 100 samples with it on the stack = 71.4% of 140 cpu samples, self 0 = 0.0%, all threads; pct of 100; self = it on top\nincl pct self self_pct frames\n100 100.0% 0 0.0% Run < Main\nomitted: none\n");
        StringAssert.Contains(focus, "## callees run\ntotal: 100 samples with it on the stack = 71.4% of 140 cpu samples, self 0 = 0.0%, all threads; pct of 100\nincl pct self self_pct frames\n" +
            "100 100.0% 0 0.0% Run\n" +
            " 70 70.0% 10 10.0% Compute\n" +
            "  60 60.0% 60 60.0% Inner\n");

        // The text report has the same trees in prose
        var prose = Report(profile, new UltraProfileReportOptions { Detailed = true, TreeThreads = 1, Hotspots = 0, MinPercent = 6, Workers = 2 });
        StringAssert.Contains(prose, "top-down call tree of 0 - main (1): 100 samples, 100 ms CPU (% of the thread's samples):\n  100 (100.0%) self 0 (0.0%)  Main > Run\n    70 (70.0%) self 10 (10.0%)  Compute\n");
        StringAssert.Contains(prose, "  left out: 10 samples (10.0%): 10 in 2 branches under 6%");
    }

    /// <summary>The row and depth limits of the trees: the heaviest rows first, what they leave out summed per row and per reason.</summary>
    [TestMethod]
    public void DetailedReportLimitsRowsAndDepth()
    {
        var profile = TreeProfile();

        var rows = Report(profile, new UltraProfileReportOptions { Compact = true, Detailed = true, TreeThreads = 1, Hotspots = 0, MinPercent = 6, MaxRows = 2, Workers = 2 });
        StringAssert.Contains(rows, "incl pct self self_pct frames\n" +
            "100 100.0% 0 0.0% Main > Run\n" +
            " 70 70.0% 10 10.0% Compute\n" +
            "  60 60.0% [+1 more callee]\n" +
            " 30 30.0% [+3 more callees]\n" +
            "omitted: 90 samples = 90.0%: 10 under 6% (2 branches), 80 over 2 rows (2 branches)\n");

        var depth = Report(profile, new UltraProfileReportOptions { Compact = true, Detailed = true, TreeThreads = 1, Hotspots = 0, MinPercent = 6, MaxDepth = 1, Workers = 2 });
        StringAssert.Contains(depth, "incl pct self self_pct frames\n" +
            "100 100.0% 0 0.0% Main > Run\n" +
            " 100 100.0% [+4 more callees]\n" +
            "omitted: 100 samples = 100.0%: 10 under 6% (2 branches), 90 beyond depth 1 (2 branches)\n");
    }

    /// <summary>
    /// The recursion collapsed: A > B > A > B > Leaf is A > B > Leaf, the samples of the inner A (A > B > A) stay A's own (the leaf
    /// stays the leaf); without it, a row per recursion level.
    /// </summary>
    [TestMethod]
    public void DetailedReportCollapsesRecursion()
    {
        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.ProfilingEndTime = 2000;
        profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];
        profile.Threads.Add(MakePathThread("0 - main (1)", "1", ("Main>A>B>A>B>Leaf", 10), ("Main>A>Leaf", 5), ("Main>A>B>A", 5)));

        var collapsed = Report(profile, new UltraProfileReportOptions { Compact = true, Detailed = true, CollapseRecursion = true, Hotspots = 1, Workers = 2 });
        StringAssert.Contains(collapsed, "recursion collapsed");
        StringAssert.Contains(collapsed, "incl pct self self_pct frames\n" +
            "20 100.0% 5 25.0% Main > A\n" +
            " 10 50.0% 10 50.0% B > Leaf\n" +
            " 5 25.0% 5 25.0% Leaf\n" +
            "omitted: none\n");
        StringAssert.Contains(collapsed, "## callers Leaf\ntotal: 15 samples with it on the stack = 75.0% of 20 cpu samples, self 15 = 75.0%");
        StringAssert.Contains(collapsed, "15 100.0% 15 100.0% Leaf\n 10 66.7% 10 66.7% B < A < Main\n 5 33.3% 5 33.3% A < Main\n");

        var levels = Report(profile, new UltraProfileReportOptions { Compact = true, Detailed = true, Hotspots = 0, Workers = 2 });
        StringAssert.Contains(levels, "incl pct self self_pct frames\n" +
            "20 100.0% 0 0.0% Main > A\n" +
            " 15 75.0% 5 25.0% B > A\n" +
            "  10 50.0% 10 50.0% B > Leaf\n" +
            " 5 25.0% 5 25.0% Leaf\n");
    }

    /// <summary>
    /// The self samples of a focus count every frame of the matching functions, the recursive and nested ones too, in the callers and
    /// the callees alike (a regression: the callees counted only the outermost frames); the rows of the trees keep their own self.
    /// </summary>
    [TestMethod]
    public void FocusSelfCountsRecursiveAndNestedFrames()
    {
        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.ProfilingEndTime = 2000;
        profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];
        profile.Threads.Add(MakePathThread("0 - main (1)", "1", ("Root>Target", 1), ("Root>Target>Target", 1)));

        var recursive = Report(profile, new UltraProfileReportOptions { Compact = true, Focus = "Target", TreeThreads = 0, Workers = 2 });
        StringAssert.Contains(recursive, "## callers Target\ntotal: 2 samples with it on the stack = 100.0% of 2 cpu samples, self 2 = 100.0%, all threads;");
        StringAssert.Contains(recursive, "incl pct self self_pct frames\n2 100.0% 2 100.0% Target\n 1 50.0% 1 50.0% Root\n 1 50.0% 1 50.0% Target < Root\nomitted: none\n");
        StringAssert.Contains(recursive, "## callees Target\ntotal: 2 samples with it on the stack = 100.0% of 2 cpu samples, self 2 = 100.0%, all threads;");
        StringAssert.Contains(recursive, "incl pct self self_pct frames\n2 100.0% 1 50.0% Target\n 1 50.0% 1 50.0% Target\nomitted: none\n");

        // "Target" matches TargetA and TargetB, TargetB called by TargetA: the callees start at TargetA only
        profile.Threads[0] = MakePathThread("0 - main (1)", "1", ("Root>TargetA", 1), ("Root>TargetA>TargetB", 1));
        var nested = Report(profile, new UltraProfileReportOptions { Compact = true, Focus = "Target", TreeThreads = 0, Workers = 2 });
        StringAssert.Contains(nested, "## callers Target\ntotal: 2 samples with it on the stack = 100.0% of 2 cpu samples, self 2 = 100.0%, all threads, 2 functions match;");
        StringAssert.Contains(nested, "## callees Target\ntotal: 2 samples with it on the stack = 100.0% of 2 cpu samples, self 2 = 100.0%, all threads, 2 functions match;");
        StringAssert.Contains(nested, "incl pct self self_pct frames\n2 100.0% 1 50.0% TargetA\n 1 50.0% 1 50.0% TargetB\nomitted: none\n");
    }

    /// <summary>
    /// The callers' rows that run to the thread start print the way to it once: a later row stops at the first frame whose way an
    /// earlier row printed ("...(N frames as above)"), unless that frame has two ways in the tree (then the rows stay whole).
    /// </summary>
    [TestMethod]
    public void CallersRowsShortenTheWayToTheThreadStart()
    {
        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.ProfilingEndTime = 2000;
        profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];
        profile.Threads.Add(MakePathThread("0 - main (1)", "1", ("Start>Entry>Main>Run>Compute>Inner", 60), ("Start>Entry>Main>Run>Load>Inner", 20)));

        var text = Report(profile, new UltraProfileReportOptions { Compact = true, Detailed = true, TreeThreads = 0, Hotspots = 1, Workers = 2 });
        StringAssert.Contains(text, "f < ...(N frames as above) = the N frames after f to the thread start");
        StringAssert.Contains(text, "incl pct self self_pct frames\n" +
            "80 100.0% 80 100.0% Inner\n" +
            " 60 75.0% 60 75.0% Compute < Run < Main < Entry < Start\n" +
            " 20 25.0% 20 25.0% Load < Run < ...(3 frames as above)\n" +
            "omitted: none\n");

        // X has two ways to the start (M < S1 and N < S2): W's row stays whole, although its way is the first one
        profile.Threads[0] = MakePathThread("0 - main (1)", "1", ("S1>M>X>Y>Leaf", 30), ("S2>N>X>Z>Leaf", 20), ("S1>M>X>W>Leaf", 10));
        var ambiguous = Report(profile, new UltraProfileReportOptions { Compact = true, Detailed = true, TreeThreads = 0, Hotspots = 1, Workers = 2 });
        StringAssert.Contains(ambiguous, "incl pct self self_pct frames\n" +
            "60 100.0% 60 100.0% Leaf\n" +
            " 30 50.0% 30 50.0% Y < X < M < S1\n" +
            " 20 33.3% 20 33.3% Z < X < N < S2\n" +
            " 10 16.7% 10 16.7% W < X < M < S1\n");
    }

    /// <summary>The code offsets list --top rows per function, as the other lists, with what they leave out.</summary>
    [TestMethod]
    public void CodeOffsetsFollowTop()
    {
        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.ProfilingEndTime = 2000;
        profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];
        profile.Threads.Add(MakeThread("0 - worker (10)", "10", samples: [("Inner", 0, 1_000_000), ("Inner", 0, 1_000_000), ("Inner", 8, 1_000_000), ("Inner", 16, 1_000_000)], weights: null));

        var compact = Report(profile, new UltraProfileReportOptions { Top = 2, CodeOffsets = true, Compact = true, Workers = 2 });
        StringAssert.Contains(compact, "## code offsets Inner\ntotal: 4 samples with an address, 3 offsets; pct of those samples\noffset samples pct\n+0x0 2 50.0%\n+0x8 1 25.0%\nomitted: 1 offset, 1 sample = 25.0%\n");

        var prose = Report(profile, new UltraProfileReportOptions { Top = 2, CodeOffsets = true, Workers = 2 });
        StringAssert.Contains(prose, "  +0x0: 2 (50.0%)\n  +0x8: 1 (25.0%)\n");
        Assert.IsFalse(prose.Contains("+0x10:"), "2 offsets with --top 2");
    }

    /// <summary>
    /// Code offsets that wrap negative in the trace's int32 (JIT'd code far from its module's base: a 2026-11-09 Windows run lost all
    /// of them) are kept and printed as unsigned; -1 is no address, and a top function without any says so instead of vanishing.
    /// </summary>
    [TestMethod]
    public void CodeOffsetsKeepNegativeAddresses()
    {
        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.ProfilingEndTime = 2000;
        profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];
        profile.Threads.Add(MakeThread("0 - worker (10)", "10", samples: [("Inner", -16, 1_000_000), ("Inner", -16, 1_000_000), ("Inner", -8, 1_000_000), ("Inner", -1, 1_000_000)], weights: null));
        profile.Threads.Add(MakeThread("1 - main (9)", "9", samples: [("Other", -1, 1_000_000)], weights: null));

        var compact = Report(profile, new UltraProfileReportOptions { CodeOffsets = true, Compact = true, Workers = 2 });
        StringAssert.Contains(compact, "## code offsets Inner\ntotal: 3 samples with an address, 2 offsets; pct of those samples\noffset samples pct\n+0xfffffff0 2 66.7%\n+0xfffffff8 1 33.3%\nomitted: none\n");
        StringAssert.Contains(compact, "## code offsets Other\nnone: its frames have no code address in the trace\n");

        var prose = Report(profile, new UltraProfileReportOptions { CodeOffsets = true, Workers = 2 });
        StringAssert.Contains(prose, "hot code offsets in Inner (3 samples with an address, 2 distinct):\n  +0xfffffff0: 2 (66.7%)\n  +0xfffffff8: 1 (33.3%)\n");
        StringAssert.Contains(prose, "hot code offsets in Other: none, its frames have no code address in the trace\n");
    }

    /// <summary>
    /// The code versions of a function (the frame's implementation: Tier0, Tier1, OSR...) keep their offsets apart, each an offset in
    /// the code of its version, with the share of the function's samples each version ran; a trace without them prints as before.
    /// </summary>
    [TestMethod]
    public void CodeOffsetsPerCodeVersion()
    {
        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.ProfilingEndTime = 2000;
        profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];
        // frame i + 1 is the leaf of sample i (frame 0 is Main)
        var thread = MakeThread("0 - worker (10)", "10", samples: [("Hot", 0x10, 1_000_000), ("Hot", 0x10, 1_000_000), ("Hot", 0x10, 1_000_000), ("Hot", 0x20, 1_000_000), ("Hot", 0x10, 1_000_000)], weights: null);
        string[] versions = ["Tier0", "Tier1", "Tier1", "Tier1", "OSR"];
        for (int i = 0; i < versions.Length; i++)
        {
            thread.StringArray.Add(versions[i]);
            thread.FrameTable.Implementation[i + 1] = thread.StringArray.Count - 1;
        }
        profile.Threads.Add(thread);

        var compact = Report(profile, new UltraProfileReportOptions { CodeOffsets = true, Compact = true, Workers = 2 });
        StringAssert.Contains(compact, "## code offsets Hot\n" +
            "code versions: Tier1 3 = 60.0%, OSR 1 = 20.0%, Tier0 1 = 20.0% of its self samples; offsets from the start of each version's code\n" +
            "total: 5 samples with an address, 4 offsets; pct of those samples\n" +
            "offset version samples pct\n" +
            "+0x10 Tier1 2 40.0%\n" +
            "+0x10 OSR 1 20.0%\n" +
            "+0x10 Tier0 1 20.0%\n" +
            "+0x20 Tier1 1 20.0%\n" +
            "omitted: none\n");

        var prose = Report(profile, new UltraProfileReportOptions { CodeOffsets = true, Workers = 2 });
        StringAssert.Contains(prose, "hot code offsets in Hot (5 samples with an address, 4 distinct; code versions: Tier1 60.0%, OSR 20.0%, Tier0 20.0%):\n  +0x10 Tier1: 2 (40.0%)\n  +0x10 OSR: 1 (20.0%)\n");
    }

    /// <summary>A time window keeps the samples and the markers that start in it: the CPU, the functions, the GC and the allocations.</summary>
    [TestMethod]
    public void WindowKeepsItsSamplesAndMarkers()
    {
        const long MiB = 1024 * 1024;
        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.ProfilingEndTime = 2000;
        profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];
        // samples at 1, 2, 3 and 4 ms
        var worker = MakeThread("0 - worker (10)", "10", samples: [("Early", 0, 1_000_000), ("Inner", 0, 1_000_000), ("Inner", 0, 1_000_000), ("Late", 0, 1_000_000)], weights: null);
        AddMarker(worker, "0 - GC Alloc (10)", 1, 1, new GCAllocationTickEvent { AllocationAmount = 5 * MiB, AllocationKind = "Small", TypeName = "System.Byte[]" });
        AddMarker(worker, "0 - GC Alloc (10)", 2.5, 2.5, new GCAllocationTickEvent { AllocationAmount = MiB, AllocationKind = "Small", TypeName = "System.String" });
        AddMarker(worker, "GC Suspend EE", 2.6, 2.7, new GCSuspendExecutionEngineEvent { Reason = "SuspendForGC", Count = 1 });
        AddMarker(worker, "GC Event", 2.7, 2.9, new GCEvent { Reason = "AllocSmall", Count = 1, Depth = 0, GCType = "NonConcurrentGC" });
        AddMarker(worker, "GC Restart EE", 2.9, 3.6, null);
        AddMarker(worker, "GC Event", 3.8, 3.9, new GCEvent { Reason = "AllocSmall", Count = 2, Depth = 1, GCType = "NonConcurrentGC" });
        profile.Threads.Add(worker);

        var text = Report(profile, new UltraProfileReportOptions { Compact = true, StartMs = 2, EndMs = 3, Workers = 2 });

        StringAssert.Contains(text, "window=[2..3]ms = 0.0s: only its samples and markers");
        StringAssert.Contains(text, "cpu_ms pct samples thread\n2 100.0% 2 0 - worker (10)\n");
        StringAssert.Contains(text, "samples pct function\n2 100.0% Inner\nomitted: none\n");
        // the suspension in the window is paired with the restart that ends after it
        StringAssert.Contains(text, "collections: 1 (gen0 1, gen1 0, gen2 0, background 0)\nreasons: AllocSmall 1\ngc pauses (managed threads stopped): 1, 1.0ms = 100.00% of 0.0s");
        StringAssert.Contains(text, "total: 1.0MB = 1000.0MB/s, 1 tick (");
        StringAssert.Contains(text, "1.0 100.0% 1 System.String\n");
    }

    /// <summary>The report of a profile with the given options, the line ends normalized.</summary>
    private static string Report(FirefoxProfiler.Profile profile, UltraProfileReportOptions options)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(profile, FirefoxProfiler.JsonProfilerContext.Default.Profile);
        var output = new StringWriter();
        new UltraProfileReport(options).Run(new MemoryStream(json), output);
        return output.ToString().Replace("\r\n", "\n");
    }

    /// <summary>
    /// Two threads with call paths: main (100 samples) Main > Run > Compute > Inner 60, Compute 10, Load > Inner 20, Tiny 5, Tiny2 5;
    /// side (40 samples) Side > Inner 30, Side > Other 10.
    /// </summary>
    private static FirefoxProfiler.Profile TreeProfile()
    {
        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.ProfilingEndTime = 2000;
        profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];
        profile.Threads.Add(MakePathThread("0 - main (1)", "1", ("Main>Run>Compute>Inner", 60), ("Main>Run>Compute", 10), ("Main>Run>Load>Inner", 20), ("Main>Run>Tiny", 5), ("Main>Run>Tiny2", 5)));
        profile.Threads.Add(MakePathThread("1 - side (2)", "2", ("Side>Inner", 30), ("Side>Other", 10)));
        return profile;
    }

    /// <summary>A thread whose samples are call paths (root first, "&gt;" between the functions), 1 ms of CPU each.</summary>
    private static FirefoxProfiler.Thread MakePathThread(string name, string tid, params (string Path, int Count)[] samples)
    {
        var thread = new FirefoxProfiler.Thread { Name = name, ProcessName = "Test (10)", Pid = "10", Tid = tid, ProcessType = "default" };
        var funcs = new Dictionary<string, int>();
        var stacks = new Dictionary<(int? Prefix, int Func), int>();
        thread.Samples.TimeDeltas = new List<double>();
        thread.Samples.ThreadCPUDelta = new List<int?>();
        foreach (var (path, count) in samples)
        {
            int? stack = null;
            foreach (var function in path.Split('>'))
            {
                if (!funcs.TryGetValue(function, out var func))
                {
                    thread.StringArray.Add(function);
                    func = funcs[function] = thread.FuncTable.Length;
                    thread.FuncTable.Name.Add(thread.StringArray.Count - 1);
                    thread.FuncTable.IsJS.Add(false);
                    thread.FuncTable.RelevantForJS.Add(false);
                    thread.FuncTable.Resource.Add(-1);
                    thread.FuncTable.FileName.Add(null);
                    thread.FuncTable.LineNumber.Add(null);
                    thread.FuncTable.ColumnNumber.Add(null);
                    thread.FuncTable.Length++;
                    thread.FrameTable.Func.Add(func);
                    thread.FrameTable.Address.Add(-1);
                    thread.FrameTable.InlineDepth.Add(0);
                    thread.FrameTable.Category.Add(0);
                    thread.FrameTable.Subcategory.Add(0);
                    thread.FrameTable.NativeSymbol.Add(null);
                    thread.FrameTable.InnerWindowID.Add(null);
                    thread.FrameTable.Implementation.Add(null);
                    thread.FrameTable.Line.Add(null);
                    thread.FrameTable.Column.Add(null);
                    thread.FrameTable.Length++;
                }
                if (!stacks.TryGetValue((stack, func), out var next))
                {
                    next = stacks[(stack, func)] = thread.StackTable.Length;
                    thread.StackTable.Frame.Add(func);   // one frame per function
                    thread.StackTable.Prefix.Add(stack);
                    thread.StackTable.Category.Add(0);
                    thread.StackTable.Subcategory.Add(0);
                    thread.StackTable.Length++;
                }
                stack = next;
            }
            for (int i = 0; i < count; i++)
            {
                thread.Samples.Stack.Add(stack);
                thread.Samples.TimeDeltas.Add(1);
                thread.Samples.ThreadCPUDelta.Add(1_000_000);
                thread.Samples.Length++;
            }
        }
        return thread;
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
