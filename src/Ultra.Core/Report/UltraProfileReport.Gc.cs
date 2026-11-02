// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Globalization;
using System.Text.Json;
using Ultra.Core.Markers;

namespace Ultra.Core.Report;

/// <summary>
/// The .NET GC part of the report, from the GC markers the converters put on the threads (ETW, nettrace and macOS alike): the
/// collections per generation and reason, the pauses (from the start of a runtime suspension to the end of the restart that follows),
/// the GC heap after the collections, and the allocation ticks (the runtime samples one allocation about every 100 KB allocated,
/// with the bytes allocated since the previous tick) per thread, type and kind.
/// </summary>
public sealed partial class UltraProfileReport
{
    private const double BytesPerMB = 1024 * 1024;

    /// <summary>The name of the restart markers, which have no payload.</summary>
    private const string RestartMarkerName = "GC Restart EE";

    private enum GcMarkerKind : byte
    {
        Collection,
        Suspend,
        Restart,
        Allocation,
        HeapStats,
        HeapSnapshot,
    }

    /// <summary>The payload fields of a GC marker that the report uses.</summary>
    private sealed record GcMarker(GcMarkerKind Kind, string? Reason = null, string? GcType = null, int Depth = 0, string? TypeName = null, string? AllocationKind = null, long Amount = 0, long[]? Generations = null, long[]? Counts = null);

    /// <summary>The markers of a thread: the GC payloads (null for the others), the names and the times; the arrays are pooled.</summary>
    private sealed record GcMarkers(List<GcMarker?> Data, int[]? Name, int NameCount, double[]? Start, int StartCount, double[]? End, int EndCount);

    private static partial class ThreadParser
    {
        /// <summary>The "markers" table of a thread: the GC payloads of "data" (by their type), "name", "startTime" and "endTime".</summary>
        private static GcMarkers ReadMarkers(ref Utf8JsonReader reader)
        {
            var data = new List<GcMarker?>();
            int[]? names = null;
            double[]? start = null, end = null;
            int nameCount = 0, startCount = 0, endCount = 0;
            reader.Read(); // { (or null)
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("data"u8))
                    {
                        reader.Read(); // [
                        if (reader.TokenType != JsonTokenType.StartArray)
                        {
                            reader.TrySkip();
                            continue;
                        }
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) data.Add(ReadGcMarker(ref reader));
                    }
                    else if (reader.ValueTextEquals("name"u8)) (names, nameCount) = ReadInts(ref reader);
                    else if (reader.ValueTextEquals("startTime"u8)) (start, startCount) = ReadDoubles(ref reader);
                    else if (reader.ValueTextEquals("endTime"u8)) (end, endCount) = ReadDoubles(ref reader);
                    else Skip(ref reader);
                }
            }
            return new GcMarkers(data, names, nameCount, start, startCount, end, endCount);
        }

        /// <summary>One element of "data", the reader on its first token: a GC payload, else null (skipped).</summary>
        private static GcMarker? ReadGcMarker(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.TrySkip();
                return null;
            }

            string? type = null, reason = null, gcType = null, typeName = null, allocationKind = null;
            long amount = 0, totalHeapSize = 0;
            int depth = 0;
            Span<long> generations = stackalloc long[5];
            Span<long> counts = stackalloc long[6];   // the heap snapshot summary
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("type"u8)) type = ReadString(ref reader);
                else if (reader.ValueTextEquals("reason"u8)) reason = ReadString(ref reader);
                else if (reader.ValueTextEquals("gcType"u8)) gcType = ReadString(ref reader);
                else if (reader.ValueTextEquals("depth"u8)) depth = (int)ReadLong(ref reader);
                else if (reader.ValueTextEquals("typeName"u8)) typeName = ReadString(ref reader);
                else if (reader.ValueTextEquals("allocationKind"u8)) allocationKind = ReadString(ref reader);
                else if (reader.ValueTextEquals("allocationAmount"u8)) amount = ReadLong(ref reader);
                else if (reader.ValueTextEquals("totalHeapSize"u8)) totalHeapSize = ReadLong(ref reader);
                else if (reader.ValueTextEquals("generationSize0"u8)) generations[0] = ReadLong(ref reader);
                else if (reader.ValueTextEquals("generationSize1"u8)) generations[1] = ReadLong(ref reader);
                else if (reader.ValueTextEquals("generationSize2"u8)) generations[2] = ReadLong(ref reader);
                else if (reader.ValueTextEquals("generationSize3"u8)) generations[3] = ReadLong(ref reader);
                else if (reader.ValueTextEquals("generationSize4"u8)) generations[4] = ReadLong(ref reader);
                else if (reader.ValueTextEquals("objects"u8)) counts[0] = ReadLong(ref reader);
                else if (reader.ValueTextEquals("bytes"u8)) counts[1] = ReadLong(ref reader);
                else if (reader.ValueTextEquals("eventsLost"u8)) counts[2] = ReadLong(ref reader);
                else if (reader.ValueTextEquals("objectBlocksLost"u8)) counts[3] = ReadLong(ref reader);
                else if (reader.ValueTextEquals("referenceBlocksLost"u8)) counts[4] = ReadLong(ref reader);
                else if (reader.ValueTextEquals("unknownReferences"u8)) counts[5] = ReadLong(ref reader);
                else Skip(ref reader);
            }

            return type switch
            {
                GCEvent.TypeId => new GcMarker(GcMarkerKind.Collection, Reason: reason, GcType: gcType, Depth: depth),
                GCSuspendExecutionEngineEvent.TypeId => new GcMarker(GcMarkerKind.Suspend, Reason: reason),
                GCRestartExecutionEngineEvent.TypeId => new GcMarker(GcMarkerKind.Restart),
                GCAllocationTickEvent.TypeId => new GcMarker(GcMarkerKind.Allocation, TypeName: typeName, AllocationKind: allocationKind, Amount: amount),
                GCHeapStatsEvent.TypeId => new GcMarker(GcMarkerKind.HeapStats, Amount: totalHeapSize, Generations: generations.ToArray()),
                HeapSnapshotEvent.TypeId => new GcMarker(GcMarkerKind.HeapSnapshot, Counts: counts.ToArray()),
                _ => null,
            };
        }

        private static string? ReadString(ref Utf8JsonReader reader)
        {
            reader.Read();
            return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        }

        private static long ReadLong(ref Utf8JsonReader reader)
        {
            reader.Read();
            return reader.TokenType == JsonTokenType.Number ? (reader.TryGetInt64(out var v) ? v : (long)reader.GetDouble()) : 0;
        }

        /// <summary>An array of numbers into a pooled double array; null becomes NaN.</summary>
        private static (double[] Array, int Count) ReadDoubles(ref Utf8JsonReader reader)
        {
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray) return (System.Buffers.ArrayPool<double>.Shared.Rent(0), 0);
            var array = System.Buffers.ArrayPool<double>.Shared.Rent(4096);
            int count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (count == array.Length) Grow(ref array);
                array[count++] = reader.TokenType == JsonTokenType.Number ? reader.GetDouble() : double.NaN;
            }
            return (array, count);
        }
    }

    /// <summary>The GC tallies of one worker, merged at the end.</summary>
    private sealed class GcTally
    {
        public readonly long[] Collections = new long[3];   // per generation (the depth of the collection)
        public int Background;
        public double BlockingMs, BlockingMaxMs, BackgroundMs;
        public readonly Dictionary<string, long> Reasons = new();
        public int Suspensions;   // for a GC
        public double SuspendMs, SuspendMaxMs;
        public readonly List<(double Start, bool ForGc)> SuspendStarts = new();
        public readonly List<double> RestartEnds = new();
        public long AllocationTicks, AllocatedBytes;
        public readonly Dictionary<string, (long Bytes, long Ticks)> AllocationsByThread = new(), AllocationsByType = new();
        public readonly Dictionary<string, long> AllocationsByKind = new();
        public long HeapMax, HeapLast;
        public long[] HeapMaxGenerations = [];
        public double HeapLastTime = double.NegativeInfinity;

        public bool Any => Collections.Sum() > 0 || Suspensions > 0 || AllocationTicks > 0 || HeapMax > 0;

        /// <summary>Adds the GC markers of a thread; its allocations only when <paramref name="allocations"/> (the thread filter).</summary>
        public void Add(string thread, string[] strings, GcMarkers markers, bool allocations)
        {
            for (int i = 0; i < markers.Data.Count; i++)
            {
                var marker = markers.Data[i];
                if (marker == null)
                {
                    // the restart markers have no payload: known by their name
                    var nameIndex = markers.Name != null && i < markers.NameCount ? markers.Name[i] : -1;
                    if (nameIndex < 0 || nameIndex >= strings.Length || strings[nameIndex] != RestartMarkerName) continue;
                    marker = new GcMarker(GcMarkerKind.Restart);
                }

                double start = markers.Start != null && i < markers.StartCount ? markers.Start[i] : double.NaN;
                double end = markers.End != null && i < markers.EndCount ? markers.End[i] : double.NaN;
                double ms = end >= start ? end - start : 0;   // false for NaN
                switch (marker.Kind)
                {
                    case GcMarkerKind.Collection:
                        Collections[Math.Clamp(marker.Depth, 0, 2)]++;
                        if (marker.GcType == "BackgroundGC")
                        {
                            Background++;
                            BackgroundMs += ms;
                        }
                        else
                        {
                            BlockingMs += ms;
                            BlockingMaxMs = Math.Max(BlockingMaxMs, ms);
                        }
                        var reason = string.IsNullOrEmpty(marker.Reason) ? "?" : marker.Reason;
                        Reasons[reason] = Reasons.GetValueOrDefault(reason) + 1;
                        break;
                    case GcMarkerKind.Suspend:
                        // SuspendForGC and SuspendForGCPrep; the runtime also stops the threads for other reasons (SuspendOther:
                        // tiered compilation, the debugger...)
                        var forGc = string.IsNullOrEmpty(marker.Reason) || marker.Reason.StartsWith("SuspendForGC", StringComparison.Ordinal);
                        if (forGc)
                        {
                            Suspensions++;
                            SuspendMs += ms;
                            SuspendMaxMs = Math.Max(SuspendMaxMs, ms);
                        }
                        if (!double.IsNaN(start)) SuspendStarts.Add((start, forGc));
                        break;
                    case GcMarkerKind.Restart:
                        if (!double.IsNaN(end)) RestartEnds.Add(end);
                        break;
                    case GcMarkerKind.Allocation when allocations:
                        AllocationTicks++;
                        AllocatedBytes += marker.Amount;
                        AddTo(AllocationsByThread, thread, marker.Amount);
                        AddTo(AllocationsByType, string.IsNullOrEmpty(marker.TypeName) ? "(unknown type)" : marker.TypeName, marker.Amount);
                        var kind = string.IsNullOrEmpty(marker.AllocationKind) ? "?" : marker.AllocationKind;
                        AllocationsByKind[kind] = AllocationsByKind.GetValueOrDefault(kind) + marker.Amount;
                        break;
                    case GcMarkerKind.HeapStats:
                        if (marker.Amount > HeapMax)
                        {
                            HeapMax = marker.Amount;
                            HeapMaxGenerations = marker.Generations ?? [];
                        }
                        if (start > HeapLastTime)
                        {
                            HeapLastTime = start;
                            HeapLast = marker.Amount;
                        }
                        break;
                }
            }
        }

        private static void AddTo(Dictionary<string, (long Bytes, long Ticks)> map, string key, long bytes)
        {
            var (b, t) = map.GetValueOrDefault(key);
            map[key] = (b + bytes, t + 1);
        }

        /// <summary>
        /// The pauses: from the start of each runtime suspension to the end of the first restart after it (before the next suspension),
        /// the time the managed threads were stopped; apart for a GC and for the other reasons.
        /// </summary>
        public (List<double> ForGc, List<double> Other) Pauses()
        {
            SuspendStarts.Sort();
            RestartEnds.Sort();
            List<double> forGc = new(), other = new();
            int r = 0;
            for (int s = 0; s < SuspendStarts.Count; s++)
            {
                var (start, isGc) = SuspendStarts[s];
                var next = s + 1 < SuspendStarts.Count ? SuspendStarts[s + 1].Start : double.PositiveInfinity;
                while (r < RestartEnds.Count && RestartEnds[r] < start) r++;
                if (r < RestartEnds.Count && RestartEnds[r] <= next)
                {
                    (isGc ? forGc : other).Add(RestartEnds[r] - start);
                    r++;
                }
            }
            return (forGc, other);
        }

        public static GcTally Merge(IEnumerable<GcTally> parts)
        {
            var all = new GcTally();
            foreach (var p in parts)
            {
                for (int g = 0; g < 3; g++) all.Collections[g] += p.Collections[g];
                all.Background += p.Background;
                all.BlockingMs += p.BlockingMs;
                all.BlockingMaxMs = Math.Max(all.BlockingMaxMs, p.BlockingMaxMs);
                all.BackgroundMs += p.BackgroundMs;
                foreach (var (k, v) in p.Reasons) all.Reasons[k] = all.Reasons.GetValueOrDefault(k) + v;
                all.Suspensions += p.Suspensions;
                all.SuspendMs += p.SuspendMs;
                all.SuspendMaxMs = Math.Max(all.SuspendMaxMs, p.SuspendMaxMs);
                all.SuspendStarts.AddRange(p.SuspendStarts);
                all.RestartEnds.AddRange(p.RestartEnds);
                all.AllocationTicks += p.AllocationTicks;
                all.AllocatedBytes += p.AllocatedBytes;
                foreach (var (k, (b, t)) in p.AllocationsByThread)
                {
                    var (b0, t0) = all.AllocationsByThread.GetValueOrDefault(k);
                    all.AllocationsByThread[k] = (b0 + b, t0 + t);
                }
                foreach (var (k, (b, t)) in p.AllocationsByType)
                {
                    var (b0, t0) = all.AllocationsByType.GetValueOrDefault(k);
                    all.AllocationsByType[k] = (b0 + b, t0 + t);
                }
                foreach (var (k, v) in p.AllocationsByKind) all.AllocationsByKind[k] = all.AllocationsByKind.GetValueOrDefault(k) + v;
                if (p.HeapMax > all.HeapMax)
                {
                    all.HeapMax = p.HeapMax;
                    all.HeapMaxGenerations = p.HeapMaxGenerations;
                }
                if (p.HeapLastTime > all.HeapLastTime)
                {
                    all.HeapLastTime = p.HeapLastTime;
                    all.HeapLast = p.HeapLast;
                }
            }
            return all;
        }
    }

    private void PrintGc(ProfileMeta meta, GcTally gc, TextWriter o)
    {
        void Line(FormattableString text) => o.WriteLine(text.ToString(CultureInfo.InvariantCulture));
        static double MB(long bytes) => bytes / BytesPerMB;

        o.WriteLine();
        if (!gc.Any)
        {
            o.WriteLine("GC: no GC or allocation events in the profile (not a .NET process, or no CLR events)");
            return;
        }

        var collections = gc.Collections.Sum();
        if (collections == 0) o.WriteLine("GC: no collection during the profile");
        else Line($"GC: {collections:N0} collections (gen0 {gc.Collections[0]:N0}, gen1 {gc.Collections[1]:N0}, gen2 {gc.Collections[2]:N0}{(gc.Background > 0 ? FormattableString.Invariant($", {gc.Background:N0} of them background") : "")})");
        if (gc.Reasons.Count > 0)
        {
            Line($"  reasons: {string.Join(", ", gc.Reasons.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => FormattableString.Invariant($"{x.Key} {x.Value:N0}")))}");
        }

        var (pauses, otherPauses) = gc.Pauses();
        if (pauses.Count > 0)
        {
            var total = pauses.Sum();
            Line($"  GC pauses (managed threads stopped): {pauses.Count:N0}, {total:F1} ms = {total * 100 / Math.Max(1, meta.DurationMs):F2}% of the {meta.DurationMs / 1000:F1} s, longest {pauses.Max():F2} ms, of which {gc.SuspendMs:F1} ms suspending the threads (longest {gc.SuspendMaxMs:F2} ms)");
        }
        else if (gc.Suspensions > 0)
        {
            Line($"  GC suspensions: {gc.Suspensions:N0}, {gc.SuspendMs:F1} ms suspending the threads (longest {gc.SuspendMaxMs:F2} ms)");
        }
        if (otherPauses.Count > 0)
        {
            Line($"  other runtime pauses (not for a GC: tiered compilation, debugger...): {otherPauses.Count:N0}, {otherPauses.Sum():F1} ms, longest {otherPauses.Max():F2} ms");
        }

        if (collections > 0)
        {
            Line($"  in blocking collections: {gc.BlockingMs:F1} ms (longest {gc.BlockingMaxMs:F2} ms){(gc.Background > 0 ? FormattableString.Invariant($"; the background collections ran {gc.BackgroundMs:F1} ms beside the program") : "")}");
        }

        if (gc.HeapMax > 0)
        {
            var g = gc.HeapMaxGenerations;
            long Gen(int i) => i < g.Length ? g[i] : 0;
            Line($"  GC heap: largest {MB(gc.HeapMax):N1} MB after a collection (gen0 {MB(Gen(0)):N1}, gen1 {MB(Gen(1)):N1}, gen2 {MB(Gen(2)):N1}, LOH {MB(Gen(3)):N1}, POH {MB(Gen(4)):N1} MB), {MB(gc.HeapLast):N1} MB after the last one");
        }

        if (gc.AllocationTicks == 0)
        {
            o.WriteLine();
            o.WriteLine(ThreadFilter != null ? "allocations: no allocation ticks in the threads of the filter" : "allocations: no allocation ticks in the profile");
            return;
        }

        var allocated = gc.AllocatedBytes;
        double Share(long bytes) => allocated > 0 ? bytes * 100.0 / allocated : 0;
        var kinds = string.Join(", ", gc.AllocationsByKind.OrderByDescending(x => x.Value).Select(x => FormattableString.Invariant($"{x.Key} {MB(x.Value):N1} MB")));
        o.WriteLine();
        Line($"allocations: about {MB(allocated):N1} MB = {MB(allocated) / Math.Max(0.001, meta.DurationMs / 1000):N1} MB/s, from {gc.AllocationTicks:N0} allocation ticks ({kinds}); the runtime samples one allocation about every 100 KB, so the types are estimates");
        o.WriteLine("per thread (MB allocated, share, ticks):");
        var threads = gc.AllocationsByThread.OrderByDescending(x => x.Value.Bytes).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        foreach (var (name, (bytes, ticks)) in threads.Take(Top))
        {
            Line($"  {name,-44} {MB(bytes),10:N1} MB {Share(bytes),5:F1}% {ticks,9:N0}");
        }
        if (threads.Count > Top)
        {
            var rest = threads.Skip(Top).Sum(x => x.Value.Bytes);
            Line($"  {FormattableString.Invariant($"... {threads.Count - Top} more threads"),-44} {MB(rest),10:N1} MB {Share(rest),5:F1}%");
        }

        o.WriteLine("top allocated types (MB allocated, share, ticks):");
        foreach (var (type, (bytes, ticks)) in gc.AllocationsByType.OrderByDescending(x => x.Value.Bytes).ThenBy(x => x.Key, StringComparer.Ordinal).Take(Top))
        {
            Line($"  {MB(bytes),10:N1} MB {Share(bytes),5:F1}% {ticks,9:N0}  {type}");
        }
    }
}
