// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Globalization;

namespace Ultra.Core.Report;

/// <summary>
/// The compact text of the report (<c>--compact</c>), for agents: a short <c>## section</c> header, a <c>total:</c> line with the units
/// and the denominator of the percents, a row of column names carrying the units, unpadded rows with the name last (numbers without
/// thousands separators), and an <c>omitted:</c> line saying how many items and how much each list leaves out. The function names
/// lose their parameters when no other function of the profile has the same name without them (an overload keeps them).
/// </summary>
public sealed partial class UltraProfileReport
{
    /// <summary>The function names without their parameters: the full name when only one function has it, null when several do.</summary>
    private Dictionary<string, string?>? _shortNames;

    private void BuildShortNames(Tally[] tallies)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tally in tallies)
        {
            foreach (var full in tally.FunctionNames)
            {
                var name = ShortName(full);
                if (!map.TryGetValue(name, out var existing)) map.Add(name, full);
                else if (existing != null && existing != full) map[name] = null;
            }
        }
        _shortNames = map;
    }

    /// <summary>A function name without its parameters ("Ns.Type.Method(int32,class System.String)" -> "Ns.Type.Method").</summary>
    private static string ShortName(string name)
    {
        if (name.Length == 0 || name[^1] != ')') return name;
        var paren = name.IndexOf('(');
        return paren > 0 ? name[..paren] : name;
    }

    /// <summary>A function name as printed: in the compact text without its parameters, unless another function has the same name without them.</summary>
    private string Fn(string name)
    {
        if (_shortNames == null) return name;
        var shortName = ShortName(name);
        return ReferenceEquals(shortName, name) || (_shortNames.TryGetValue(shortName, out var full) && full == name) ? shortName : name;
    }

    /// <summary>A share in percent with one decimal and its sign.</summary>
    private static string Pct(double part, double total) => (total > 0 ? part * 100 / total : 0).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A count with its word, singular for 1: "1 thread", "2 threads" (the plural adds an s unless given).</summary>
    private static string Count(long count, string singular, string? plural = null)
        => count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural ?? singular + "s");

    /// <summary>The last line of a compact list: the items it leaves out (<paramref name="item"/>, singular) and their amount, or "none".</summary>
    private static void Omitted(TextWriter o, int count, string item, FormattableString amount)
    {
        o.WriteLine(count > 0 ? "omitted: " + Count(count, item) + ", " + amount.ToString(CultureInfo.InvariantCulture) : "omitted: none");
    }

    /// <summary>The unit of the event counts of a counter: millions or thousands when large, so that the rows stay short.</summary>
    private static (double Divider, string Suffix) EventScale(long total)
        => total >= 10_000_000_000 ? (1e6, "M") : total >= 10_000_000 ? (1e3, "K") : (1, "");

    private void PrintCompact(ProfileMeta meta, Totals totals, Tally[] tallies, TextWriter o)
    {
        var (cpuThreads, cpuLeaf, counters, threads, totalCpu) = totals;
        var duration = DurationMs(meta);
        void Line(FormattableString text) => o.WriteLine(text.ToString(CultureInfo.InvariantCulture));

        o.WriteLine("## profile");
        Line($"product={meta.Product} abi={meta.Abi} cpus={meta.Cpus} duration={meta.DurationMs / 1000:F1}s{(meta.IntervalMs > 0 ? FormattableString.Invariant($" sampling={1000 / meta.IntervalMs:F0}/s") : "")}");
        if (Window is { } window) Line($"window={window} = {duration / 1000:F1}s: only its samples and markers (heap snapshot whole)");
        if (ThreadFilter != null) Line($"thread filter: names containing \"{ThreadFilter}\" (GC lines: whole process)");
        var counterTracks = string.Join(", ", counters.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => FormattableString.Invariant($"{x.Key} {x.Value.Tracks}")));
        var trackCounts = $"{Count(cpuThreads.Count, "cpu thread")}, {Count(counters.Values.Sum(x => x.Tracks), "counter track")}{(counters.Count > 0 ? ": " + counterTracks : "")}";
        o.WriteLine(ThreadFilter != null ? FormattableString.Invariant($"tracks: {threads} in the file; in the filter: {trackCounts}") : FormattableString.Invariant($"tracks: {threads} ({trackCounts})"));

        o.WriteLine();
        o.WriteLine("## cpu by thread");
        var rows = totals.ThreadsByCpu;
        Line($"total: {totalCpu / 1000:F1}s cpu = {totalCpu / Math.Max(1, duration):F2} cores avg over {duration / 1000:F1}s, {Count(cpuThreads.Sum(x => (long)x.Samples), "sample")}, {Count(rows.Count, "thread")}; pct of total cpu");
        o.WriteLine("cpu_ms pct samples thread");
        foreach (var (name, n, cpu) in rows.Take(Top))
        {
            Line($"{cpu:F0} {Pct(cpu, totalCpu)} {n} {name}");
        }
        var restCpu = rows.Skip(Top).Sum(x => x.CpuMs);
        Omitted(o, rows.Count - Top, "thread", $"{restCpu:F0}ms = {Pct(restCpu, totalCpu)}");

        var leafSamples = cpuLeaf.Values.Sum();
        var leafs = cpuLeaf.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        o.WriteLine();
        o.WriteLine("## self samples by function");
        Line($"total: {Count(leafSamples, "sample")} over the cpu threads, {Count(leafs.Count, "function")}; pct of total samples");
        o.WriteLine("samples pct function");
        foreach (var (leaf, c) in leafs.Take(Top))
        {
            Line($"{c} {Pct(c, leafSamples)} {Fn(leaf)}");
        }
        var restLeaf = leafs.Skip(Top).Sum(x => x.Value);
        Omitted(o, leafs.Count - Top, "function", $"{Count(restLeaf, "sample")} = {Pct(restLeaf, leafSamples)}");

        if (CodeOffsets)
        {
            // the top functions' offsets, as many rows as --top
            foreach (var (leaf, _) in leafs.Take(3))
            {
                var (offsets, tiers) = CodeOffsetsOf(tallies, leaf);
                o.WriteLine();
                o.WriteLine($"## code offsets {Fn(leaf)}");
                if (offsets.Count == 0)
                {
                    // said, not skipped: an empty list is the trace's (no address recorded for its frames), not the report's
                    o.WriteLine("none: its frames have no code address in the trace");
                    continue;
                }
                var total = offsets.Sum(x => x.Samples);
                if (tiers.Count > 0)
                {
                    // the code versions the function ran in (a hot function still in Tier0, or a hot loop in its OSR version, shows
                    // here); each offset is in the code of its version
                    var selfSamples = tiers.Sum(x => x.Samples);
                    Line($"code versions: {string.Join(", ", tiers.Select(x => FormattableString.Invariant($"{TierLabel(x.Tier)} {x.Samples} = {Pct(x.Samples, selfSamples)}")))} of its self samples; offsets from the start of each version's code");
                }
                // the offsets as unsigned 32-bit (the trace stores them as int32)
                Line($"total: {Count(total, "sample")} with an address, {Count(offsets.Count, "offset")}; pct of those samples");
                o.WriteLine(tiers.Count > 0 ? "offset version samples pct" : "offset samples pct");
                foreach (var (tier, offset, c) in offsets.Take(Top))
                {
                    Line($"+0x{(uint)offset:x}{(tiers.Count > 0 ? " " + TierLabel(tier) : "")} {c} {Pct(c, total)}");
                }
                var restOffsets = offsets.Skip(Top).Sum(x => x.Samples);
                Omitted(o, offsets.Count - Top, "offset", $"{Count(restOffsets, "sample")} = {Pct(restOffsets, total)}");
            }
        }

        foreach (var (counter, (tracks, samples, events)) in counters.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var byLeaf = Merge(tallies.Select(t => t.CounterLeaf.GetValueOrDefault(counter)).Where(x => x != null)!)
                .OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
            var (divider, suffix) = EventScale(events);
            o.WriteLine();
            o.WriteLine($"## counter {counter}");
            Line($"total: {events / divider:F0}{suffix} events, {Count(samples, "sample")}, {Count(tracks, "track")}, {Count(byLeaf.Count, "function")}; pct of total events");
            o.WriteLine(suffix.Length > 0 ? $"events_{suffix} pct function" : "events pct function");
            foreach (var (leaf, c) in byLeaf.Take(Top))
            {
                Line($"{c / divider:F0} {Pct(c, events)} {Fn(leaf)}");
            }
            var restEvents = byLeaf.Skip(Top).Sum(x => x.Value);
            Omitted(o, byLeaf.Count - Top, "function", $"{restEvents / divider:F0}{suffix} events = {Pct(restEvents, events)}");
        }

        PrintGcCompact(meta, GcTally.Merge(tallies.Select(t => t.Gc)), o);
        PrintLocksCompact(LockTally.Merge(tallies.Select(t => t.Locks)), o);
        PrintHeapCompact(HeapTally.Merge(tallies.Select(t => t.Heap)), o);
    }
}
