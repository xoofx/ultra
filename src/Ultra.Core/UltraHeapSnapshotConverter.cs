// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Ultra.Core.Markers;

namespace Ultra.Core;

/// <summary>
/// Converts a GC heap snapshot (<see cref="UltraHeapSnapshot"/>) to two tracks of its process in a Firefox profile: <c>[heap]</c>,
/// weighted by bytes, and <c>[heap objects]</c>, weighted by objects. Their call trees are the retention paths: each live object
/// is a sample whose stack is the chain of types from a root to it, so the inclusive size of a node is what it keeps alive.
/// </summary>
/// <remarks>
/// The object graph has many paths to an object: each object is given the shortest one from the first class of roots that reaches
/// it, in the order statics, strong handles, pinned handles, stacks, finalizer queue, other roots (weak handles keep nothing alive).
/// Consecutive objects of the same type (a linked list, a tree of nodes) are folded into one frame, and a path is cut at
/// <see cref="MaxDepth"/> frames.
/// </remarks>
internal static class UltraHeapSnapshotConverter
{
    /// <summary>The counter name (in brackets in the track name) of the bytes track.</summary>
    public const string BytesCounter = "heap";

    /// <summary>The counter name of the objects track.</summary>
    public const string ObjectsCounter = "heap objects";

    private const int MaxDepth = 128;

    private const int RootStatics = 0, RootStrongHandles = 1, RootPinnedHandles = 2, RootStacks = 3, RootFinalizer = 4, RootOther = 5, RootUnreached = 6;

    private static readonly string[] RootNames = ["[statics]", "[strong handles]", "[pinned handles]", "[stacks]", "[finalizer queue]", "[other roots]", "[not reached from a root]"];

    /// <summary>
    /// Adds the heap tracks of the snapshot file to the profile of the process.
    /// </summary>
    public static void AddTo(FirefoxProfiler.Profile profile, string nettraceFile, int processId, UltraProfilerOptions options)
    {
        options.LogProgress?.Invoke($"Converting the GC heap snapshot {Path.GetFileName(nettraceFile)}");
        AddTo(profile, HeapGraph.Read(nettraceFile), processId, options, Path.GetFileName(nettraceFile));
    }

    /// <summary>
    /// Adds the heap tracks of a snapshot to the profile of the process.
    /// </summary>
    internal static void AddTo(FirefoxProfiler.Profile profile, HeapGraph heap, int processId, UltraProfilerOptions options, string label)
    {
        if (heap.Count == 0)
        {
            (options.LogWarning ?? options.LogProgress)?.Invoke($"Warning: the GC heap snapshot {label} has no objects");
            return;
        }

        // The build consumes the raw columns of the graph
        var objectCount = heap.Count;
        var totalBytes = heap.TotalBytes;
        var (eventsLost, objectBlocksLost, referenceBlocksLost, placeholders) = (heap.EventsLost, heap.ObjectBlocksLost, heap.ReferenceBlocksLost, heap.PlaceholderReferences);
        options.LogProgress?.Invoke($"GC heap snapshot events: {heap.ObjectBlocks:N0} object blocks, {heap.ReferenceBlocks:N0} reference blocks ({heap.EdgeTarget.Count:N0} references); roots: {heap.RootSummary()}");
        var tree = RetentionTree.Build(heap);
        var unknownReferences = tree.MissingEdges + placeholders;
        void Warn(string text) => (options.LogWarning ?? options.LogProgress)?.Invoke($"Warning: {text}");
        if (eventsLost > 0 || objectBlocksLost > 0 || referenceBlocksLost > 0)
        {
            Warn($"the GC heap snapshot {label} lost {eventsLost:N0} events while it was taken (the EventPipe buffer of the process was full): " +
                 $"{objectBlocksLost:N0} object blocks and {referenceBlocksLost:N0} reference blocks are missing, {unknownReferences:N0} references are unknown " +
                 "(the missing objects are left out, the objects they held may be under [not reached from a root])");
        }
        else if (tree.MissingEdges > 0)
        {
            Warn($"the GC heap snapshot {label} lacks {tree.MissingEdges:N0} of the references its objects announce, without any event lost: some retention paths are wrong");
        }

        var pid = processId.ToString(CultureInfo.InvariantCulture);
        var processThread = profile.Threads.FirstOrDefault(t => t.Pid == pid);
        var time = profile.Meta.ProfilingEndTime ?? 0;
        var bytesThread = tree.ToThread($"GC heap snapshot [{BytesCounter}]", $"{pid}.heap", pid, processThread, time, tree.Bytes, "bytes");

        // The summary and diagnosis of the snapshot, carried by the trace (read by `ultra report`)
        if (!profile.Meta.MarkerSchema.Any(x => x.Name == HeapSnapshotEvent.TypeId)) profile.Meta.MarkerSchema.Add(HeapSnapshotEvent.Schema());
        var markers = bytesThread.Markers;
        bytesThread.StringArray.Add("GC heap snapshot");
        markers.Name.Add(bytesThread.StringArray.Count - 1);
        markers.StartTime.Add(time);
        markers.EndTime.Add(time);
        markers.Phase.Add(FirefoxProfiler.MarkerPhase.Instance);
        markers.Category.Add(UltraConverterToFirefox.CategoryGc);
        markers.ThreadId.Add(profile.Threads.Count);
        markers.Data.Add(new HeapSnapshotEvent
        {
            Objects = objectCount,
            Bytes = totalBytes,
            EventsLost = eventsLost,
            ObjectBlocksLost = objectBlocksLost,
            ReferenceBlocksLost = referenceBlocksLost,
            UnknownReferences = unknownReferences,
        });
        markers.Length++;
        profile.Threads.Add(bytesThread);
        profile.Threads.Add(tree.ToThread($"GC heap snapshot [{ObjectsCounter}]", $"{pid}.heapobjects", pid, processThread, time, tree.Objects, "samples"));
        options.LogProgress?.Invoke($"GC heap snapshot: {objectCount:N0} objects, {totalBytes / (1024.0 * 1024.0):N1} MB, {tree.Objects.Count(x => x > 0):N0} retention paths; {eventsLost:N0} events lost, {objectBlocksLost:N0} object blocks and {referenceBlocksLost:N0} reference blocks missing, {unknownReferences:N0} unknown references");
    }

    /// <summary>
    /// An empty profile for heap snapshots converted alone (without the CPU trace): the categories of the other converters (the
    /// heap frames are in <see cref="UltraConverterToFirefox.CategoryGc"/>), the product from the file name (<c>ultra_&lt;name&gt;_...</c>).
    /// </summary>
    public static FirefoxProfiler.Profile CreateProfile(string nettraceFile)
    {
        var name = Path.GetFileName(nettraceFile);
        var parts = name.Split('_');
        var product = parts.Length > 2 && parts[0] == "ultra" ? parts[1] : Path.GetFileNameWithoutExtension(name);
        var profile = new FirefoxProfiler.Profile
        {
            Meta =
            {
                StartTime = new DateTimeOffset(File.GetLastWriteTimeUtc(nettraceFile)).ToUnixTimeMilliseconds(),
                ProfilingStartTime = 0,
                ProfilingEndTime = 0,
                Version = 29,
                PreprocessedProfileVersion = 51,
                Product = product,
                InitialSelectedThreads = [],
                InitialVisibleThreads = [],
                DoesNotUseFrameImplementation = true,
                Symbolicated = true,
                SampleUnits = new FirefoxProfiler.SampleUnits { Time = "ms", EventDelay = "ms", ThreadCPUDelta = "ns" },
                Interval = 1,
                Abi = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Categories = [],
            }
        };

        foreach (var (category, color) in new[]
                 {
                     ("Other", FirefoxProfiler.ProfileColor.Grey), ("Kernel", FirefoxProfiler.ProfileColor.Orange), ("Native", FirefoxProfiler.ProfileColor.Blue),
                     (".NET", FirefoxProfiler.ProfileColor.Green), (".NET GC", FirefoxProfiler.ProfileColor.Yellow), (".NET JIT", FirefoxProfiler.ProfileColor.Purple),
                     (".NET CLR", FirefoxProfiler.ProfileColor.Grey),
                 })
        {
            profile.Meta.Categories.Add(new FirefoxProfiler.Category { Name = category, Color = color, Subcategories = { "Other" } });
        }
        return profile;
    }

    /// <summary>The objects, references and roots of a heap snapshot, as read from its events (or added by a test).</summary>
    internal sealed class HeapGraph
    {
        public readonly List<ulong> Address = new();
        public readonly List<long> Size = new();
        public readonly List<ulong> TypeId = new();
        public readonly List<int> EdgeCount = new();
        public readonly List<ulong> EdgeTarget = new();
        public readonly List<(ulong Address, int RootClass)> Roots = new();
        public readonly List<(ulong Address, string Field)> Statics = new();
        public readonly List<(ulong Key, ulong Value)> DependentEdges = new();
        private readonly Dictionary<ulong, string> _typeNameById = new();

        public int Count => Address.Count;

        public long TotalBytes;

        /// <summary>The events the runtime dropped (the nettrace sequence numbers tell).</summary>
        public long EventsLost;

        /// <summary>The object blocks missing (gaps in the GCBulkNode indexes): their objects are not in the graph.</summary>
        public long ObjectBlocksLost;

        /// <summary>The reference blocks missing (gaps in the GCBulkEdge indexes), each replaced by placeholder references.</summary>
        public long ReferenceBlocksLost;

        /// <summary>The placeholder references put in place of the missing reference blocks.</summary>
        public long PlaceholderReferences;

        private int _lastNodeBlock = -1, _lastEdgeBlock = -1, _largestEdgeBlock;

        /// <summary>Starts an object block (a GCBulkNode event): counts the blocks missing before it.</summary>
        public void BeginNodeBlock(int index)
        {
            if (index > _lastNodeBlock + 1) ObjectBlocksLost += index - _lastNodeBlock - 1;
            _lastNodeBlock = Math.Max(_lastNodeBlock, index);
        }

        /// <summary>
        /// Starts a reference block (a GCBulkEdge event) of <paramref name="count"/> references. The references are paired with the
        /// objects in order, so a missing block would shift the references of all the objects after it: it is replaced by
        /// placeholders (unknown references), as many as a full block holds (all the blocks are full but the last).
        /// </summary>
        public void BeginEdgeBlock(int index, int count)
        {
            if (index > _lastEdgeBlock + 1)
            {
                var missing = index - _lastEdgeBlock - 1;
                var placeholders = (long)missing * Math.Max(_largestEdgeBlock, count);
                ReferenceBlocksLost += missing;
                PlaceholderReferences += placeholders;
                for (long i = 0; i < placeholders; i++) EdgeTarget.Add(0);
            }
            _lastEdgeBlock = Math.Max(_lastEdgeBlock, index);
            _largestEdgeBlock = Math.Max(_largestEdgeBlock, count);
        }

        public void AddType(ulong typeId, string name)
        {
            if (!string.IsNullOrEmpty(name)) _typeNameById.TryAdd(typeId, name);
        }

        /// <summary>An object; its <paramref name="edgeCount"/> references are the next ones of <see cref="AddEdge"/>.</summary>
        public void AddNode(ulong address, long size, ulong typeId, int edgeCount)
        {
            Address.Add(address);
            Size.Add(size);
            TypeId.Add(typeId);
            EdgeCount.Add(edgeCount);
            TotalBytes += size;
        }

        public void AddEdge(ulong target) => EdgeTarget.Add(target);

        /// <summary>The roots received, by class (statics apart), then the weak and the "older" ones ignored: for the log.</summary>
        public readonly long[] RootCounts = new long[RootNames.Length + 2];

        public long ReferenceBlocks, ObjectBlocks, RootEvents;

        /// <summary>The roots, for the log: e.g. "330 statics, 12 strong handles, ... (ignored: 2,000,000 weak handles)".</summary>
        public string RootSummary()
        {
            var parts = new List<string> { $"{Statics.Count:N0} statics" };
            for (int c = RootStrongHandles; c <= RootOther; c++) parts.Add($"{RootCounts[c]:N0} {RootNames[c].Trim('[', ']')}");
            return $"{string.Join(", ", parts)} (ignored: {RootCounts[RootNames.Length]:N0} weak handles, {RootCounts[RootNames.Length + 1]:N0} older) from {RootEvents:N0} root events";
        }

        public void AddRoot(ulong address, GCRootKind kind, GCRootFlags flags)
        {
            // A weak handle keeps nothing alive; "older" roots only exist in gen0/gen1 collections
            if ((flags & GCRootFlags.WeakRef) != 0)
            {
                RootCounts[RootNames.Length]++;
                return;
            }
            if (kind == GCRootKind.Older)
            {
                RootCounts[RootNames.Length + 1]++;
                return;
            }
            var rootClass = kind switch
            {
                GCRootKind.Stack => RootStacks,
                GCRootKind.Finalizer => RootFinalizer,
                GCRootKind.Handle => (flags & GCRootFlags.Pinning) != 0 ? RootPinnedHandles : RootStrongHandles,
                _ => RootOther,
            };
            RootCounts[rootClass]++;
            Roots.Add((address, rootClass));
        }

        public void AddStatic(ulong address, string field) => Statics.Add((address, field));

        /// <summary>A ConditionalWeakTable entry: the value is kept alive by its key.</summary>
        public void AddDependent(ulong key, ulong value) => DependentEdges.Add((key, value));

        public string TypeName(ulong typeId) => _typeNameById.TryGetValue(typeId, out var name) ? name : $"0x{typeId:x}";

        public static HeapGraph Read(string nettraceFile)
        {
            var graph = new HeapGraph();
            using var source = new EventPipeEventSource(nettraceFile);
            source.Clr.TypeBulkType += e =>
            {
                for (int i = 0; i < e.Count; i++)
                {
                    var v = e.Values(i);
                    graph.AddType(v.TypeID, v.TypeName);
                }
            };
            source.Clr.GCBulkNode += e =>
            {
                graph.ObjectBlocks++;
                graph.BeginNodeBlock(e.Index);
                for (int i = 0; i < e.Count; i++)
                {
                    var v = e.Values(i);
                    graph.AddNode(v.Address, (long)v.Size, v.TypeID, (int)v.EdgeCount);
                }
            };
            source.Clr.GCBulkEdge += e =>
            {
                graph.ReferenceBlocks++;
                graph.BeginEdgeBlock(e.Index, e.Count);
                for (int i = 0; i < e.Count; i++) graph.AddEdge(e.Values(i).Target);
            };
            source.Clr.GCBulkRootEdge += e =>
            {
                graph.RootEvents++;
                for (int i = 0; i < e.Count; i++)
                {
                    var v = e.Values(i);
                    graph.AddRoot(v.RootedNodeAddress, v.GCRootKind, v.GCRootFlag);
                }
            };
            source.Clr.GCBulkRootStaticVar += e =>
            {
                for (int i = 0; i < e.Count; i++)
                {
                    var v = e.Values(i);
                    graph.AddStatic(v.ObjectID, v.FieldName);
                }
            };
            source.Clr.GCBulkRootConditionalWeakTableElementEdge += e =>
            {
                for (int i = 0; i < e.Count; i++)
                {
                    var v = e.Values(i);
                    graph.AddDependent(v.GCKeyNodeID, v.GCValueNodeID);
                }
            };
            source.Process();
            graph.EventsLost = source.EventsLost;
            return graph;
        }
    }

    /// <summary>The retention paths of the objects, folded into stacks of type names, with the bytes and objects of each.</summary>
    internal sealed class RetentionTree
    {
        private readonly List<string> _funcNames = new();
        private readonly List<int> _stackFunc = new();
        private readonly List<int> _stackPrefix = new();
        private readonly List<int> _stackDepth = new();
        private readonly Dictionary<long, int> _stacks = new();
        private readonly Dictionary<string, int> _extraFuncs = new(StringComparer.Ordinal);

        public long[] Bytes = [];
        public long[] Objects = [];
        public long MissingEdges;

        public int StackCount => _stackFunc.Count;

        /// <param name="heap">The graph, whose raw columns are consumed.</param>
        /// <param name="maxThreads">The threads that resolve the references (0: all the processors). The result does not depend on it.</param>
        public static RetentionTree Build(HeapGraph heap, int maxThreads = 0)
        {
            var tree = new RetentionTree();
            int n = heap.Count;

            // One func per type (the type names may come after the objects in the events, so resolved here)
            var types = new int[n];
            var typeFuncs = new Dictionary<ulong, int>();
            for (int i = 0; i < n; i++)
            {
                var typeId = heap.TypeId[i];
                if (!typeFuncs.TryGetValue(typeId, out var func))
                {
                    func = tree._funcNames.Count;
                    tree._funcNames.Add(heap.TypeName(typeId));
                    typeFuncs.Add(typeId, func);
                }
                types[i] = func;
            }
            var rootFuncs = RootNames.Select(tree.Func).ToArray();

            // The raw columns are released as soon as their indexed copy exists: a heap of tens of millions of objects and
            // references holds GBs of them (the graph is consumed by this build)
            Release(heap.TypeId);

            // Objects by address, for the references (and roots) to them
            var sortedAddress = heap.Address.ToArray();
            Release(heap.Address);
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(sortedAddress, order);
            var sizes = heap.Size;
            int Find(ulong address)
            {
                if (address == 0) return -1;
                int i = Array.BinarySearch(sortedAddress, address);
                if (i >= 0) return order[i];
                i = ~i - 1;   // an interior pointer: the object that contains it
                return i >= 0 && address < sortedAddress[i] + (ulong)sizes[order[i]] ? order[i] : -1;
            }

            // The references of each object: its EdgeCount next edges, in the order of the events
            var edgeStart = new int[n + 1];
            long edgeTotal = 0;
            for (int i = 0; i < n; i++)
            {
                edgeStart[i] = (int)Math.Min(edgeTotal, heap.EdgeTarget.Count);
                edgeTotal += heap.EdgeCount[i];
            }
            edgeStart[n] = (int)Math.Min(edgeTotal, heap.EdgeTarget.Count);
            tree.MissingEdges = Math.Max(0, edgeTotal - heap.EdgeTarget.Count);
            Release(heap.EdgeCount);
            // A binary search per reference, each independent: in parallel over ranges (the walk below stays serial, the
            // shortest paths and the root order decide which path an object gets)
            var edgeTo = new int[edgeStart[n]];
            var targets = heap.EdgeTarget;
            const int range = 1 << 16;
            Parallel.For(0, (edgeTo.Length + range - 1) / range, new ParallelOptions { MaxDegreeOfParallelism = maxThreads > 0 ? maxThreads : -1 }, r =>
            {
                for (int e = r * range, end = Math.Min(edgeTo.Length, e + range); e < end; e++) edgeTo[e] = Find(targets[e]);
            });
            Release(heap.EdgeTarget);

            // ConditionalWeakTable: the value is kept alive by its key
            var dependents = new Dictionary<int, List<int>>();
            foreach (var (key, value) in heap.DependentEdges)
            {
                int k = Find(key), v = Find(value);
                if (k < 0 || v < 0) continue;
                if (!dependents.TryGetValue(k, out var list)) dependents[k] = list = new();
                list.Add(v);
            }

            var nodeStack = new int[n];
            Array.Fill(nodeStack, -1);
            var queue = new int[n];
            int head = 0, tail = 0;

            void Claim(int node, int stack)
            {
                if (node < 0 || nodeStack[node] >= 0) return;
                nodeStack[node] = tree.Push(stack, types[node]);
                queue[tail++] = node;
            }

            void Walk()
            {
                while (head < tail)
                {
                    var node = queue[head++];
                    var stack = nodeStack[node];
                    for (int e = edgeStart[node]; e < edgeStart[node + 1]; e++) Claim(edgeTo[e], stack);
                    if (dependents.TryGetValue(node, out var values))
                    {
                        foreach (var v in values) Claim(v, stack);
                    }
                }
            }

            // The roots claim the objects class by class: a class reaches all it can before the next one
            var staticsRoot = tree.Push(-1, rootFuncs[RootStatics]);
            foreach (var (address, field) in heap.Statics)
            {
                // The event gives the type of the value, not the class of the field: the value's type is the next frame
                Claim(Find(address), tree.Push(staticsRoot, tree.Func($"static {field}")));
            }
            Walk();
            for (int rootClass = RootStrongHandles; rootClass <= RootOther; rootClass++)
            {
                var rootStack = tree.Push(-1, rootFuncs[rootClass]);
                foreach (var (address, c) in heap.Roots)
                {
                    if (c == rootClass) Claim(Find(address), rootStack);
                }
                Walk();
            }

            // Objects no root reached (a root kind not reported, or lost events): their own structure under one pseudo root
            var unreachedRoot = tree.Push(-1, rootFuncs[RootUnreached]);
            for (int node = 0; node < n; node++)
            {
                if (nodeStack[node] >= 0) continue;
                Claim(node, unreachedRoot);
                Walk();
            }

            tree.Bytes = new long[tree.StackCount];
            tree.Objects = new long[tree.StackCount];
            for (int node = 0; node < n; node++)
            {
                tree.Bytes[nodeStack[node]] += sizes[node];
                tree.Objects[nodeStack[node]]++;
            }
            return tree;
        }

        private static void Release<T>(List<T> column)
        {
            column.Clear();
            column.TrimExcess();
        }

        private int Func(string name)
        {
            if (!_extraFuncs.TryGetValue(name, out var index))
            {
                index = _funcNames.Count;
                _funcNames.Add(name);
                _extraFuncs.Add(name, index);
            }
            return index;
        }

        /// <summary>The stack of <paramref name="func"/> under <paramref name="prefix"/> (-1 for a root): the prefix itself for the same type again, or past the maximum depth.</summary>
        private int Push(int prefix, int func)
        {
            if (prefix >= 0 && (_stackFunc[prefix] == func || _stackDepth[prefix] >= MaxDepth)) return prefix;
            var key = ((long)(prefix + 1) << 32) | (uint)func;
            if (!_stacks.TryGetValue(key, out var stack))
            {
                stack = _stackFunc.Count;
                _stackFunc.Add(func);
                _stackPrefix.Add(prefix);
                _stackDepth.Add(prefix >= 0 ? _stackDepth[prefix] + 1 : 1);
                _stacks.Add(key, stack);
            }
            return stack;
        }

        /// <summary>A Firefox thread with one sample per retention path that holds objects, weighted by <paramref name="weights"/>.</summary>
        public FirefoxProfiler.Thread ToThread(string name, string tid, string pid, FirefoxProfiler.Thread? processThread, double time, long[] weights, string weightType)
        {
            var thread = new FirefoxProfiler.Thread
            {
                Name = name,
                ProcessName = processThread?.ProcessName,
                ProcessStartupTime = processThread?.ProcessStartupTime ?? 0,
                RegisterTime = processThread?.ProcessStartupTime ?? 0,
                ProcessShutdownTime = processThread?.ProcessShutdownTime,
                UnregisterTime = processThread?.ProcessShutdownTime,
                ProcessType = "default",
                Pid = pid,
                Tid = tid,
                ShowMarkersInTimeline = false,
            };

            // One func and one frame per type or root name (frame i = func i)
            foreach (var funcName in _funcNames)
            {
                var func = thread.FuncTable.Length;
                thread.StringArray.Add(funcName);
                thread.FuncTable.Name.Add(func);
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
                thread.FrameTable.Category.Add(UltraConverterToFirefox.CategoryGc);
                thread.FrameTable.Subcategory.Add(0);
                thread.FrameTable.NativeSymbol.Add(null);
                thread.FrameTable.InnerWindowID.Add(null);
                thread.FrameTable.Implementation.Add(null);
                thread.FrameTable.Line.Add(null);
                thread.FrameTable.Column.Add(null);
                thread.FrameTable.Length++;
            }

            for (int s = 0; s < _stackFunc.Count; s++)
            {
                thread.StackTable.Frame.Add(_stackFunc[s]);
                thread.StackTable.Prefix.Add(_stackPrefix[s] >= 0 ? _stackPrefix[s] : null);
                thread.StackTable.Category.Add(UltraConverterToFirefox.CategoryGc);
                thread.StackTable.Subcategory.Add(0);
                thread.StackTable.Length++;
            }

            var samples = thread.Samples;
            samples.TimeDeltas = new List<double>();
            samples.Weight = new List<long>();
            samples.WeightType = weightType;
            for (int s = 0; s < weights.Length; s++)
            {
                if (weights[s] == 0) continue;
                samples.TimeDeltas.Add(samples.Length == 0 ? time : 0);
                samples.Stack.Add(s);
                samples.Weight.Add(weights[s]);
                samples.Length++;
            }
            return thread;
        }
    }
}
