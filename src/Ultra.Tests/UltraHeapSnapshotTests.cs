// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Ultra.Core;
using Ultra.Core.Report;

namespace Ultra.Tests;

[TestClass]
public class UltraHeapSnapshotTests
{
    private const long MiB = 1024 * 1024;

    /// <summary>
    /// A heap built by hand, converted to the [heap] tracks and read back by the report: the first root class that reaches an object
    /// holds it (the static before the stack root to the same node), a weak handle holds nothing, a ConditionalWeakTable value is
    /// held through its key, an interior pointer finds its object, and a linked list is one frame.
    /// </summary>
    [TestMethod]
    public void RetentionPathsGoToTheFirstRootClassAndFoldRepeatedTypes()
    {
        var heap = new UltraHeapSnapshotConverter.HeapGraph();
        ulong A(int i) => (ulong)i << 32;   // addresses far apart: sizes never overlap
        heap.AddType(1, "Model");
        heap.AddType(2, "Node");
        heap.AddType(3, "System.Double[]");
        heap.AddType(4, "Link");
        heap.AddType(5, "Cache");
        heap.AddType(6, "System.Byte[]");

        // static Model -> Model -> 2 Nodes -> a double[] each; the ConditionalWeakTable keeps a byte[] alive through the Model
        heap.AddNode(A(1), MiB, 1, 2);
        heap.AddEdge(A(2));
        heap.AddEdge(A(3));
        heap.AddNode(A(2), MiB, 2, 1);
        heap.AddEdge(A(4));
        heap.AddNode(A(3), MiB, 2, 1);
        heap.AddEdge(A(5));
        heap.AddNode(A(4), 10 * MiB, 3, 0);
        heap.AddNode(A(5), 10 * MiB, 3, 0);
        heap.AddNode(A(6), 3 * MiB, 6, 0);
        heap.AddStatic(A(1), "Model");
        heap.AddDependent(A(1), A(6));
        heap.AddRoot(A(2), GCRootKind.Stack, GCRootFlags.None);   // already held by the static

        // a strong handle to a linked list of 3 Links; a stack root with an interior pointer into the last one
        heap.AddNode(A(7), MiB, 4, 1);
        heap.AddEdge(A(8));
        heap.AddNode(A(8), MiB, 4, 1);
        heap.AddEdge(A(9));
        heap.AddNode(A(9), MiB, 4, 0);
        heap.AddRoot(A(7), GCRootKind.Handle, GCRootFlags.None);
        heap.AddRoot(A(9) + 8, GCRootKind.Stack, GCRootFlags.Interior);

        // a pinned byte[]; a Cache only a weak handle points to
        heap.AddNode(A(10), 5 * MiB, 6, 0);
        heap.AddRoot(A(10), GCRootKind.Handle, GCRootFlags.Pinning);
        heap.AddNode(A(11), 2 * MiB, 5, 0);
        heap.AddRoot(A(11), GCRootKind.Handle, GCRootFlags.WeakRef);

        var profile = new FirefoxProfiler.Profile();
        profile.Meta.Product = "Test";
        profile.Meta.ProfilingEndTime = 1000;
        profile.Meta.Categories = [new FirefoxProfiler.Category { Name = "Other", Color = FirefoxProfiler.ProfileColor.Grey, Subcategories = { "Other" } }];
        UltraHeapSnapshotConverter.AddTo(profile, heap, 10, new UltraProfilerOptions(), "test");
        Assert.AreEqual(2, profile.Threads.Count);
        Assert.AreEqual("GC heap snapshot [heap]", profile.Threads[0].Name);
        Assert.AreEqual("bytes", profile.Threads[0].Samples.WeightType);

        var json = JsonSerializer.SerializeToUtf8Bytes(profile, FirefoxProfiler.JsonProfilerContext.Default.Profile);
        var output = new StringWriter();
        new UltraProfileReport(top: 10, workers: 2).Run(new MemoryStream(json), output);
        var text = output.ToString();

        StringAssert.Contains(text, "GC heap snapshot (the live objects of a full blocking GC after the profiling): 36.0 MB in 11 objects, 7 retention paths");
        StringAssert.Contains(text, "      26.0 MB  72.2%  [statics]");
        StringAssert.Contains(text, "       5.0 MB  13.9%  [pinned handles]");
        StringAssert.Contains(text, "       3.0 MB   8.3%  [strong handles]");
        StringAssert.Contains(text, "       2.0 MB   5.6%  [not reached from a root]");
        StringAssert.Contains(text, "      20.0 MB  55.6%          2  System.Double[]");
        StringAssert.Contains(text, "       8.0 MB  22.2%          2  System.Byte[]");
        StringAssert.Contains(text, "       3.0 MB   8.3%          3  Link");
        StringAssert.Contains(text, "      26.0 MB  72.2%  static Model");
        StringAssert.Contains(text, "      26.0 MB  72.2%  Model");
        StringAssert.Contains(text, "      22.0 MB  61.1%  Node");
        Assert.IsFalse(text.Contains("[stacks]"), "the stack roots reached nothing new");
    }

    /// <summary>
    /// A reference block lost (its index skipped): placeholders keep the references after it with their own objects, the objects the
    /// lost references held go to [not reached from a root], and the report says what was lost.
    /// </summary>
    [TestMethod]
    public void ALostReferenceBlockDoesNotShiftTheOtherReferences()
    {
        var heap = new UltraHeapSnapshotConverter.HeapGraph();
        ulong A(int i) => (ulong)i << 32;
        for (ulong t = 0; t < 6; t++) heap.AddType(t, $"T{t}");

        // a chain T0 -> T1 -> ... -> T5 of 1 MB objects; the references come in blocks of 2: [0->1, 1->2] [2->3, 3->4] (lost) [4->5]
        heap.BeginNodeBlock(0);
        for (int i = 0; i < 6; i++) heap.AddNode(A(i + 1), MiB, (ulong)i, i < 5 ? 1 : 0);
        heap.BeginEdgeBlock(0, 2);
        heap.AddEdge(A(2));
        heap.AddEdge(A(3));
        heap.BeginEdgeBlock(2, 1);
        heap.AddEdge(A(6));
        heap.AddStatic(A(1), "Chain");
        heap.EventsLost = 1;

        var profile = new FirefoxProfiler.Profile();
        profile.Meta.ProfilingEndTime = 1000;
        UltraHeapSnapshotConverter.AddTo(profile, heap, 10, new UltraProfilerOptions(), "test");
        var json = JsonSerializer.SerializeToUtf8Bytes(profile, FirefoxProfiler.JsonProfilerContext.Default.Profile);
        var output = new StringWriter();
        new UltraProfileReport(top: 10, workers: 2).Run(new MemoryStream(json), output);
        var text = output.ToString();

        StringAssert.Contains(text, "WARNING: 1 events lost while it was taken (the EventPipe buffer of the process was full): 0 object blocks and 1 reference blocks missing, 2 unknown references");
        StringAssert.Contains(text, "       3.0 MB  50.0%  [statics]");
        StringAssert.Contains(text, "       3.0 MB  50.0%  [not reached from a root]");
        // T4 still holds T5 (its reference came after the lost block): without the placeholders it would have gone to T2
        StringAssert.Contains(text, "       2.0 MB  33.3%  T4");
        StringAssert.Contains(text, "       1.0 MB  16.7%  T2");
    }

    /// <summary>The references are resolved in parallel: the retention paths are the same with one thread and with several.</summary>
    [TestMethod]
    public void RetentionPathsDoNotDependOnTheThreads()
    {
        static UltraHeapSnapshotConverter.HeapGraph RandomHeap()
        {
            var random = new Random(42);
            var heap = new UltraHeapSnapshotConverter.HeapGraph();
            const int objects = 200_000;
            for (ulong t = 1; t <= 50; t++) heap.AddType(t, $"Type{t}");
            for (int i = 0; i < objects; i++)
            {
                var edges = random.Next(0, 6);
                heap.AddNode((ulong)(i + 1) * 64, 48, (ulong)random.Next(1, 51), edges);
                for (int e = 0; e < edges; e++) heap.AddEdge((ulong)(random.Next(objects) + 1) * 64 + (ulong)random.Next(0, 2) * 8);
            }
            for (int r = 0; r < 100; r++) heap.AddRoot((ulong)(random.Next(objects) + 1) * 64, (GCRootKind)random.Next(0, 3), GCRootFlags.None);
            return heap;
        }

        FirefoxProfiler.Thread Build(int threads) => UltraHeapSnapshotConverter.RetentionTree.Build(RandomHeap(), threads) is var tree
            ? tree.ToThread("t [heap]", "1.heap", "1", null, 0, tree.Bytes, "bytes")
            : throw new InvalidOperationException();

        var serial = Build(1);
        var parallel = Build(8);
        Assert.IsTrue(serial.StackTable.Length > 1000, "the graph has many paths");
        CollectionAssert.AreEqual(serial.StackTable.Frame, parallel.StackTable.Frame);
        CollectionAssert.AreEqual(serial.StackTable.Prefix, parallel.StackTable.Prefix);
        CollectionAssert.AreEqual(serial.Samples.Stack, parallel.Samples.Stack);
        CollectionAssert.AreEqual(serial.Samples.Weight, parallel.Samples.Weight);
    }

    [TestMethod]
    public void HeapSnapshotFileNamesGiveThePid()
    {
        Assert.AreEqual(1234, UltraHeapSnapshot.GetProcessId(@"C:\x\ultra_app_2026-11-02_10_00_00_pid_1234_heap.nettrace"));
        Assert.IsNull(UltraHeapSnapshot.GetProcessId("ultra_app_clr.nettrace"));
        Assert.IsTrue(new UltraProfilerTraceFile("a_pid_5_heap.nettrace").IsHeapSnapshot);
    }
}
