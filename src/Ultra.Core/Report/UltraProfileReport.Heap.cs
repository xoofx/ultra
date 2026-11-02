// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Globalization;

namespace Ultra.Core.Report;

/// <summary>
/// The GC heap snapshot part of the report (<c>--heap-snapshot</c>), from its <c>[heap]</c> and <c>[heap objects]</c> tracks, whose
/// stacks are the retention paths (root, then the types from it to the object): the bytes per root, the types by size and objects,
/// and the retainers, the types whose paths hold the most bytes.
/// </summary>
public sealed partial class UltraProfileReport
{
    /// <summary>The heap tallies of one worker, merged at the end.</summary>
    private sealed class HeapTally
    {
        public long Bytes, Objects;
        public int Paths;
        public readonly Dictionary<string, long> BytesByType = new(), ObjectsByType = new(), BytesByRoot = new(), BytesThrough = new();

        public bool Any => Bytes > 0 || Objects > 0;

        /// <summary>The snapshot summaries (HeapSnapshotEvent): objects, bytes, events lost, object blocks lost, reference blocks lost, unknown references.</summary>
        public readonly long[] Summary = new long[6];

        public void AddSummary(long[] counts)
        {
            for (int i = 0; i < Summary.Length && i < counts.Length; i++) Summary[i] += counts[i];
        }

        public void Add(bool bytes, string[] strings, int[] frameFunc, int[] stackFrame, int[]? stackPrefix, int stackPrefixCount, int[] funcName, int[] sampleStack, int sampleCount, long[]? weight, int weightCount)
        {
            string Name(int stack) => strings[funcName[frameFunc[stackFrame[stack]]]];
            var onPath = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < sampleCount; i++)
            {
                var w = weight != null && i < weightCount ? weight[i] : 1;
                var stack = sampleStack[i];
                var leaf = Name(stack);
                if (!bytes)
                {
                    Objects += w;
                    ObjectsByType[leaf] = ObjectsByType.GetValueOrDefault(leaf) + w;
                    continue;
                }

                Bytes += w;
                Paths++;
                BytesByType[leaf] = BytesByType.GetValueOrDefault(leaf) + w;

                // Up the path to the root: each type on it holds these bytes (once, a type can repeat on a path)
                onPath.Clear();
                var root = leaf;
                for (int s = stack, depth = 0; s >= 0 && depth < 1024; depth++)
                {
                    root = Name(s);
                    if (onPath.Add(root)) BytesThrough[root] = BytesThrough.GetValueOrDefault(root) + w;
                    s = stackPrefix != null && s < stackPrefixCount ? stackPrefix[s] : -1;
                }
                BytesByRoot[root] = BytesByRoot.GetValueOrDefault(root) + w;
            }
        }

        public static HeapTally Merge(IEnumerable<HeapTally> parts)
        {
            var all = new HeapTally();
            foreach (var p in parts)
            {
                all.Bytes += p.Bytes;
                all.Objects += p.Objects;
                all.Paths += p.Paths;
                all.AddSummary(p.Summary);
                AddAll(all.BytesByType, p.BytesByType);
                AddAll(all.ObjectsByType, p.ObjectsByType);
                AddAll(all.BytesByRoot, p.BytesByRoot);
                AddAll(all.BytesThrough, p.BytesThrough);
            }
            return all;
        }

        private static void AddAll(Dictionary<string, long> to, Dictionary<string, long> from)
        {
            foreach (var (k, v) in from) to[k] = to.GetValueOrDefault(k) + v;
        }
    }

    private void PrintHeap(HeapTally heap, TextWriter o)
    {
        if (!heap.Any) return;

        void Line(FormattableString text) => o.WriteLine(text.ToString(CultureInfo.InvariantCulture));
        static double MB(long bytes) => bytes / BytesPerMB;
        double Share(long bytes) => heap.Bytes > 0 ? bytes * 100.0 / heap.Bytes : 0;
        static bool IsRoot(string name) => name.StartsWith('[');

        o.WriteLine();
        Line($"GC heap snapshot (the live objects of a full blocking GC after the profiling): {MB(heap.Bytes):N1} MB in {heap.Objects:N0} objects, {heap.Paths:N0} retention paths");
        var (eventsLost, objectBlocksLost, referenceBlocksLost, unknownReferences) = (heap.Summary[2], heap.Summary[3], heap.Summary[4], heap.Summary[5]);
        if (eventsLost > 0 || objectBlocksLost > 0 || referenceBlocksLost > 0)
        {
            Line($"  WARNING: {eventsLost:N0} events lost while it was taken (the EventPipe buffer of the process was full): {objectBlocksLost:N0} object blocks and {referenceBlocksLost:N0} reference blocks missing, {unknownReferences:N0} unknown references; the missing objects are left out and the objects they held may be under [not reached from a root]");
        }
        else if (unknownReferences > 0)
        {
            Line($"  WARNING: {unknownReferences:N0} references announced by the objects are missing, without any event lost: some retention paths are wrong");
        }
        o.WriteLine("by root (MB, share):");
        foreach (var (root, bytes) in heap.BytesByRoot.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
        {
            Line($"  {MB(bytes),10:N1} MB {Share(bytes),5:F1}%  {root}");
        }

        o.WriteLine("top types by size (MB, share, objects):");
        foreach (var (type, bytes) in heap.BytesByType.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(Top))
        {
            Line($"  {MB(bytes),10:N1} MB {Share(bytes),5:F1}% {heap.ObjectsByType.GetValueOrDefault(type),10:N0}  {type}");
        }

        o.WriteLine("top retainers (MB of the objects on the shortest paths from a root through the type, share):");
        foreach (var (type, bytes) in heap.BytesThrough.Where(x => !IsRoot(x.Key)).OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(Top))
        {
            Line($"  {MB(bytes),10:N1} MB {Share(bytes),5:F1}%  {type}");
        }
    }
}
