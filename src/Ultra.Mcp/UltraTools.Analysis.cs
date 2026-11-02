// Copyright (c) 2026 Kirill Osenkov. Licensed under the MIT license.
// Imported from https://github.com/KirillOsenkov/UltraMcp (commit 434b5bc); see THIRD-PARTY-NOTICES.txt.

// Analysis tools: inclusive call tree, inverted (bottom-up) call tree,
// name-driven hotspot search, function focus (callers/callees), and
// module/category rollups. All whole-thread aggregation tools accept an
// optional [startMs, endMs] time window (trace-relative ms, same scale as
// query_markers) to localize a discrete spike or user interaction.

using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Ultra.Mcp;

internal static partial class UltraTools
{
    internal const string UnsymbolizedModule = "<none>";

    internal static List<int> WalkFuncs(FirefoxProfiler.Thread thread, int stackIndex)
    {
        var stackTable = thread.StackTable;
        var frameTable = thread.FrameTable;
        int stackCount = stackTable.Frame.Count;
        var funcs = new List<int>();
        int current = stackIndex;
        int guard = 0;
        while (current >= 0 && current < stackCount && guard++ < 200000)
        {
            int frame = stackTable.Frame[current];
            funcs.Add(frameTable.Func[frame]);
            int? prefix = stackTable.Prefix[current];
            current = prefix ?? -1;
        }

        return funcs;
    }

    // The samples of a thread within the time window, summed per stack row: the aggregation tools then walk each distinct stack
    // once (samples + unique stacks x depth, not samples x depth). A sample counts its weight when the track has weights (the
    // [counter] tracks of --pmc: events; the [heap] tracks of --heap-snapshot: bytes or objects), else 1.
    internal sealed class StackWeights
    {
        public readonly Dictionary<int, long> ByStack = new();

        public long Total;

        public long Samples;

        // The unit of the weights ("bytes", "samples"...), null when the track has none (one per sample)
        public string? WeightType;

        // "  weightType=bytes (565 samples)" for a weighted track, empty otherwise: the counts are then weights, not samples
        public string Suffix => WeightType is null ? string.Empty : $"  weightType={WeightType} ({Samples} samples)";
    }

    internal static StackWeights AggregateStacks(FirefoxProfiler.Thread thread, double? startMs, double? endMs)
    {
        var samples = thread.Samples;
        var weights = samples.Weight;
        double[]? times = MaybeSampleTimes(thread, startMs, endMs);
        int stackCount = thread.StackTable.Frame.Count;
        var result = new StackWeights { WeightType = weights is null ? null : (string.IsNullOrEmpty(samples.WeightType) ? "weight" : samples.WeightType) };
        for (int i = 0; i < samples.Stack.Count; i++)
        {
            if (samples.Stack[i] is not int si || si < 0 || si >= stackCount)
            {
                continue;
            }

            if (times != null && !InWindow(times[i], startMs, endMs))
            {
                continue;
            }

            long w = weights != null && i < weights.Count ? weights[i] : 1;
            ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(result.ByStack, si, out _);
            slot += w;
            result.Total += w;
            result.Samples++;
        }

        return result;
    }

    // Cumulative absolute timestamp (ms, trace-relative) for each sample,
    // computed by running-sum of Samples.TimeDeltas. The writer stores each
    // delta as (thisTimestamp - previousTimestamp) starting from 0, so the
    // running sum reproduces the absolute TimeStampRelativeMSec — the same ms
    // scale query_markers prints, so [startMs, endMs] windows line up.
    internal static double[] ComputeSampleTimes(FirefoxProfiler.Thread thread)
    {
        var deltas = thread.Samples.TimeDeltas;
        int n = thread.Samples.Stack.Count;
        var times = new double[n];
        double acc = 0;
        for (int i = 0; i < n; i++)
        {
            if (deltas != null && i < deltas.Count)
            {
                acc += deltas[i];
            }

            times[i] = acc;
        }

        return times;
    }

    internal static bool InWindow(double t, double? startMs, double? endMs)
        => (!startMs.HasValue || t >= startMs.Value) && (!endMs.HasValue || t <= endMs.Value);

    // Builds the per-sample time array only when a window is actually requested.
    internal static double[]? MaybeSampleTimes(FirefoxProfiler.Thread thread, double? startMs, double? endMs)
        => (startMs.HasValue || endMs.HasValue) ? ComputeSampleTimes(thread) : null;

    internal static string WindowSuffix(double? startMs, double? endMs)
    {
        if (!startMs.HasValue && !endMs.HasValue)
        {
            return string.Empty;
        }

        string lo = startMs.HasValue ? startMs.Value.ToString("0.###") : "start";
        string hi = endMs.HasValue ? endMs.Value.ToString("0.###") : "end";
        return $"  window=[{lo}..{hi}]ms";
    }

    internal static string ModuleName(FirefoxProfiler.Thread thread, int funcIndex)
    {
        var funcTable = thread.FuncTable;
        int resource = funcTable.Resource[funcIndex];
        if (resource >= 0 && resource < thread.ResourceTable.Name.Count)
        {
            return SafeString(thread, thread.ResourceTable.Name[resource]);
        }

        return string.Empty;
    }

    internal static HashSet<int> ResolveFuncSelector(FirefoxProfiler.Thread thread, string selector)
    {
        var result = new HashSet<int>();
        var s = selector.Trim();
        int funcCount = thread.FuncTable.Name.Count;
        if (s.StartsWith("fn", StringComparison.OrdinalIgnoreCase) && int.TryParse(s.AsSpan(2), out int a) && a >= 0 && a < funcCount)
        {
            result.Add(a);
            return result;
        }

        if (int.TryParse(s, out int b) && b >= 0 && b < funcCount)
        {
            result.Add(b);
            return result;
        }

        for (int f = 0; f < funcCount; f++)
        {
            if (FuncName(thread, f).IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Add(f);
            }
        }

        return result;
    }

    internal static HashSet<int> ResolveMatchingFuncs(FirefoxProfiler.Thread thread, string[] queries)
    {
        var matched = new HashSet<int>();
        int funcCount = thread.FuncTable.Name.Count;
        for (int f = 0; f < funcCount; f++)
        {
            string name = FuncName(thread, f);
            foreach (var q in queries)
            {
                if (!string.IsNullOrWhiteSpace(q) && name.IndexOf(q.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matched.Add(f);
                    break;
                }
            }
        }

        return matched;
    }

    [McpServerTool(Name = "find_hotspots", ReadOnly = true, Idempotent = true)]
    [Description("Finds where a set of named functions/types spend time across the profile. Matches each function module!name against the given substrings (case-insensitive, ANY-of) and reports self and inclusive sample counts per thread. Use this when you know a name, e.g. queries=[SimpleSplitterPanel] or queries=[DetachDocument,Reparent,TabWell]. Each line ends with a [threadIndex/fn<idx>] handle for call_tree/focus_function. Optional startMs/endMs restrict the scan to a time window (trace-relative ms, same scale as query_markers).")]
    public static string FindHotspots(
        [Description("Substrings matched against each function module!name; a function matches if it contains ANY of them.")] string[] queries,
        [Description("Optional single thread index; default scans every thread.")] int? threadIndex = null,
        [Description("Absolute path to a profile .json. Optional: defaults to the most recently loaded profile.")] string? path = null,
        [Description("Max matched functions listed per thread (default 25).")] int? maxResults = null,
        [Description("Optional inclusive lower bound on sample time (trace-relative ms).")] double? startMs = null,
        [Description("Optional inclusive upper bound on sample time (trace-relative ms).")] double? endMs = null) => Run(() =>
    {
        if (queries == null || queries.Length == 0)
        {
            throw new McpException("Provide at least one query string in queries.");
        }

        int take = Math.Clamp(maxResults ?? 25, 1, MaxAllowedResults);
        var entry = ResolveProfile(path);
        var threads = entry.Profile.Threads;

        var order = new List<int>();
        if (threadIndex.HasValue)
        {
            ResolveThread(entry, threadIndex.Value);
            order.Add(threadIndex.Value);
        }
        else
        {
            for (int i = 0; i < threads.Count; i++)
            {
                order.Add(i);
            }
        }

        var sb = new StringBuilder();
        sb.Append("queries: ").Append(string.Join(", ", queries)).AppendLine(WindowSuffix(startMs, endMs));
        bool anyThread = false;
        foreach (int ti in order)
        {
            var thread = threads[ti];
            var matched = ResolveMatchingFuncs(thread, queries);
            if (matched.Count == 0)
            {
                continue;
            }

            var stacks = AggregateStacks(thread, startMs, endMs);
            var self = new Dictionary<int, long>();
            var incl = new Dictionary<int, long>();
            long anyIncl = 0;
            long totalWithStack = stacks.Total;
            var distinct = new HashSet<int>();
            foreach (var (si, w) in stacks.ByStack)
            {
                var funcs = WalkFuncs(thread, si);
                if (funcs.Count == 0)
                {
                    continue;
                }

                if (matched.Contains(funcs[0]))
                {
                    self[funcs[0]] = self.GetValueOrDefault(funcs[0]) + w;
                }

                distinct.Clear();
                bool touched = false;
                foreach (var f in funcs)
                {
                    if (!distinct.Add(f))
                    {
                        continue;
                    }

                    if (matched.Contains(f))
                    {
                        incl[f] = incl.GetValueOrDefault(f) + w;
                        touched = true;
                    }
                }

                if (touched)
                {
                    anyIncl += w;
                }
            }

            if (incl.Count == 0 && self.Count == 0)
            {
                continue;
            }

            anyThread = true;
            double tot = totalWithStack > 0 ? totalWithStack : 1;
            sb.AppendLine();
            sb.Append("thread [").Append(ti).Append("] ").Append(thread.Name)
              .Append("  matchedFuncs=").Append(matched.Count)
              .Append("  inclusiveAny=").Append(anyIncl)
              .Append(" (").Append((100.0 * anyIncl / tot).ToString("0.0")).Append("% of ").Append(totalWithStack).Append(" sampled)")
              .Append(stacks.Suffix)
              .AppendLine();
            var ranked = matched.Where(f => incl.ContainsKey(f) || self.ContainsKey(f))
                .OrderByDescending(f => incl.GetValueOrDefault(f))
                .Take(take);
            foreach (var f in ranked)
            {
                long ic = incl.GetValueOrDefault(f);
                long sc = self.GetValueOrDefault(f);
                sb.Append("  incl ").Append(ic).Append(" (").Append((100.0 * ic / tot).ToString("0.0")).Append("%)")
                  .Append("  self ").Append(sc).Append(" (").Append((100.0 * sc / tot).ToString("0.0")).Append("%)  ")
                  .Append(FuncName(thread, f))
                  .Append("  [").Append(ti).Append("/fn").Append(f).Append(']')
                  .AppendLine();
            }
        }

        if (!anyThread)
        {
            sb.AppendLine("(no functions match the given queries in any thread" + (startMs.HasValue || endMs.HasValue ? " within the window)" : ")"));
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "call_tree", ReadOnly = true, Idempotent = true)]
    [Description("Inclusive top-down call tree for one thread: merges all sample stacks into a weighted tree so expensive subtrees (slow pockets) are visible even when their self time is small. Each line: inclSamples (pct%) self=N module!func [threadIndex/fn<idx>]. Optional focus roots the tree at a function and shows only its callees. Optional startMs/endMs restrict to a time window (trace-relative ms). For the mirror view (callers upward) use call_tree_inverted.")]
    public static string CallTree(
        [Description("Thread index (from list_threads).")] int threadIndex,
        [Description("Absolute path to a profile .json. Optional: defaults to the most recently loaded profile.")] string? path = null,
        [Description("Optional function selector (fn<idx>, a bare index, or a name substring). Roots the tree at that function (its callees).")] string? focus = null,
        [Description("Max tree depth to render (default 12).")] int? maxDepth = null,
        [Description("Prune nodes below this inclusive percent of the tree (default 1.0).")] double? minPercent = null,
        [Description("Max children shown per node (default 12).")] int? maxChildren = null,
        [Description("Optional inclusive lower bound on sample time (trace-relative ms).")] double? startMs = null,
        [Description("Optional inclusive upper bound on sample time (trace-relative ms).")] double? endMs = null) => Run(() =>
    {
        int depthLimit = Math.Clamp(maxDepth ?? 12, 1, 200);
        double minPct = minPercent ?? 1.0;
        int childCap = Math.Clamp(maxChildren ?? 12, 1, 1000);
        var entry = ResolveProfile(path);
        var thread = ResolveThread(entry, threadIndex);

        HashSet<int>? focusSet = null;
        if (!string.IsNullOrWhiteSpace(focus))
        {
            focusSet = ResolveFuncSelector(thread, focus);
            if (focusSet.Count == 0)
            {
                throw new McpException($"No function matches focus '{focus}' in thread {threadIndex}.");
            }
        }

        var root = new TreeAgg();
        var stacks = AggregateStacks(thread, startMs, endMs);
        foreach (var (si, w) in stacks.ByStack)
        {
            // leaf->root; the top-down tree goes root->leaf, so it is read backwards (no copy)
            var funcs = WalkFuncs(thread, si);
            if (funcs.Count == 0)
            {
                continue;
            }

            // The focus is the root-most matching frame: the last match in leaf->root order
            int startIdx = funcs.Count - 1;
            if (focusSet != null)
            {
                startIdx = -1;
                for (int k = funcs.Count - 1; k >= 0; k--)
                {
                    if (focusSet.Contains(funcs[k]))
                    {
                        startIdx = k;
                        break;
                    }
                }

                if (startIdx < 0)
                {
                    continue;
                }
            }

            root.Incl += w;
            var node = root;
            for (int k = startIdx; k >= 0; k--)
            {
                int f = funcs[k];
                if (!node.Children.TryGetValue(f, out var child))
                {
                    child = new TreeAgg();
                    node.Children[f] = child;
                }

                child.Incl += w;
                node = child;
            }

            node.Self += w;
        }

        double total = root.Incl > 0 ? root.Incl : 1;
        var sb = new StringBuilder();
        sb.Append("thread [").Append(threadIndex).Append("] ").Append(thread.Name)
          .Append("  samplesInTree=").Append(root.Incl).Append(stacks.Suffix);
        if (focusSet != null)
        {
            sb.Append("  focus=").Append(focus);
        }

        sb.Append(WindowSuffix(startMs, endMs)).AppendLine();
        sb.AppendLine("(incl samples, % of tree, self)");

        RenderTree(sb, root, 0, depthLimit, minPct, childCap, total, thread, threadIndex);
        return sb.ToString();
    });

    [McpServerTool(Name = "call_tree_inverted", ReadOnly = true, Idempotent = true)]
    [Description("Bottom-up (inverted) call tree for one thread: the mirror of call_tree. Roots are leaf frames (or the focus function) and children walk UPWARD through callers, so you can drill from a hot function toward whoever drives time into it, multiple levels deep in one call. Each line: inclSamples (pct%) self=N module!func [threadIndex/fn<idx>]. With focus=<name|fn<idx>> the single root is that function and its subtree is its full merged caller tree. Without focus, roots are the hottest leaf functions ranked by self samples. Optional startMs/endMs restrict to a time window.")]
    public static string CallTreeInverted(
        [Description("Thread index (from list_threads).")] int threadIndex,
        [Description("Absolute path to a profile .json. Optional: defaults to the most recently loaded profile.")] string? path = null,
        [Description("Optional function selector (fn<idx>, a bare index, or a name substring). Roots the inverted tree at that function (its callers upward).")] string? focus = null,
        [Description("Max tree depth to render (default 12).")] int? maxDepth = null,
        [Description("Prune nodes below this inclusive percent of the tree (default 1.0).")] double? minPercent = null,
        [Description("Max children shown per node (default 12).")] int? maxChildren = null,
        [Description("Optional inclusive lower bound on sample time (trace-relative ms).")] double? startMs = null,
        [Description("Optional inclusive upper bound on sample time (trace-relative ms).")] double? endMs = null) => Run(() =>
    {
        int depthLimit = Math.Clamp(maxDepth ?? 12, 1, 200);
        double minPct = minPercent ?? 1.0;
        int childCap = Math.Clamp(maxChildren ?? 12, 1, 1000);
        var entry = ResolveProfile(path);
        var thread = ResolveThread(entry, threadIndex);

        HashSet<int>? focusSet = null;
        if (!string.IsNullOrWhiteSpace(focus))
        {
            focusSet = ResolveFuncSelector(thread, focus);
            if (focusSet.Count == 0)
            {
                throw new McpException($"No function matches focus '{focus}' in thread {threadIndex}.");
            }
        }

        var root = new TreeAgg();
        var stacks = AggregateStacks(thread, startMs, endMs);
        foreach (var (si, w) in stacks.ByStack)
        {
            // funcs is leaf->root; walking increasing index climbs toward callers.
            var funcs = WalkFuncs(thread, si);
            if (funcs.Count == 0)
            {
                continue;
            }

            int startIdx = 0;
            if (focusSet != null)
            {
                startIdx = -1;
                for (int k = 0; k < funcs.Count; k++)
                {
                    if (focusSet.Contains(funcs[k]))
                    {
                        startIdx = k;
                        break;
                    }
                }

                if (startIdx < 0)
                {
                    continue;
                }
            }

            root.Incl += w;
            var node = root;
            for (int k = startIdx; k < funcs.Count; k++)
            {
                int f = funcs[k];
                if (!node.Children.TryGetValue(f, out var child))
                {
                    child = new TreeAgg();
                    node.Children[f] = child;
                }

                child.Incl += w;
                node = child;
            }

            node.Self += w;
        }

        double total = root.Incl > 0 ? root.Incl : 1;
        var sb = new StringBuilder();
        sb.Append("thread [").Append(threadIndex).Append("] ").Append(thread.Name)
          .Append("  samplesInTree=").Append(root.Incl).Append(stacks.Suffix).Append("  (inverted / bottom-up)");
        if (focusSet != null)
        {
            sb.Append("  focus=").Append(focus);
        }

        sb.Append(WindowSuffix(startMs, endMs)).AppendLine();
        sb.AppendLine("(incl samples, % of tree, self; children are CALLERS)");

        RenderTree(sb, root, 0, depthLimit, minPct, childCap, total, thread, threadIndex);
        return sb.ToString();
    });

    private static void RenderTree(
        StringBuilder sb,
        TreeAgg node,
        int depth,
        int depthLimit,
        double minPct,
        int childCap,
        double total,
        FirefoxProfiler.Thread thread,
        int threadIndex)
    {
        if (depth > depthLimit)
        {
            return;
        }

        int shown = 0;
        foreach (var kv in node.Children.OrderByDescending(kv => kv.Value.Incl))
        {
            double pct = 100.0 * kv.Value.Incl / total;
            if (pct < minPct)
            {
                break;
            }

            if (shown++ >= childCap)
            {
                sb.Append(' ', depth * 2).AppendLine("...");
                break;
            }

            sb.Append(' ', depth * 2)
              .Append(kv.Value.Incl).Append(" (").Append(pct.ToString("0.0")).Append("%)")
              .Append(" self=").Append(kv.Value.Self).Append("  ")
              .Append(FuncName(thread, kv.Key))
              .Append("  [").Append(threadIndex).Append("/fn").Append(kv.Key).Append(']')
              .AppendLine();
            RenderTree(sb, kv.Value, depth + 1, depthLimit, minPct, childCap, total, thread, threadIndex);
        }
    }

    [McpServerTool(Name = "focus_function", ReadOnly = true, Idempotent = true)]
    [Description("Drill-down for one function (or a name substring matching several): reports its self and inclusive share in a thread, its top callers (who drives time into it), and its top callees (where it spends time). Ideal after find_hotspots pinpoints a name. For a multi-level caller tree use call_tree_inverted; for multi-level callees use call_tree focus=. Optional startMs/endMs restrict to a time window.")]
    public static string FocusFunction(
        [Description("Thread index (from list_threads).")] int threadIndex,
        [Description("Function selector: fn<idx>, a bare index, or a name substring (all matches aggregated).")] string func,
        [Description("Absolute path to a profile .json. Optional: defaults to the most recently loaded profile.")] string? path = null,
        [Description("Max callers/callees listed (default 15).")] int? maxResults = null,
        [Description("Optional inclusive lower bound on sample time (trace-relative ms).")] double? startMs = null,
        [Description("Optional inclusive upper bound on sample time (trace-relative ms).")] double? endMs = null) => Run(() =>
    {
        int take = Math.Clamp(maxResults ?? 15, 1, MaxAllowedResults);
        var entry = ResolveProfile(path);
        var thread = ResolveThread(entry, threadIndex);
        var target = ResolveFuncSelector(thread, func);
        if (target.Count == 0)
        {
            throw new McpException($"No function matches '{func}' in thread {threadIndex}.");
        }

        var stacks = AggregateStacks(thread, startMs, endMs);
        long selfCount = 0, inclCount = 0, totalWithStack = stacks.Total;
        var callers = new Dictionary<int, long>();
        var callees = new Dictionary<int, long>();
        foreach (var (si, w) in stacks.ByStack)
        {
            var funcs = WalkFuncs(thread, si);
            if (funcs.Count == 0)
            {
                continue;
            }

            if (target.Contains(funcs[0]))
            {
                selfCount += w;
            }

            int topMost = -1, bottomMost = -1;
            for (int k = 0; k < funcs.Count; k++)
            {
                if (target.Contains(funcs[k]))
                {
                    if (bottomMost < 0)
                    {
                        bottomMost = k;
                    }

                    topMost = k;
                }
            }

            if (topMost < 0)
            {
                continue;
            }

            inclCount += w;
            if (topMost + 1 < funcs.Count && !target.Contains(funcs[topMost + 1]))
            {
                int caller = funcs[topMost + 1];
                callers[caller] = callers.GetValueOrDefault(caller) + w;
            }

            if (bottomMost - 1 >= 0 && !target.Contains(funcs[bottomMost - 1]))
            {
                int callee = funcs[bottomMost - 1];
                callees[callee] = callees.GetValueOrDefault(callee) + w;
            }
        }

        double tot = totalWithStack > 0 ? totalWithStack : 1;
        var sb = new StringBuilder();
        sb.Append("thread [").Append(threadIndex).Append("] ").Append(thread.Name).Append(stacks.Suffix).AppendLine(WindowSuffix(startMs, endMs));
        sb.Append("matchedFunctions: ").Append(target.Count).AppendLine();
        foreach (var f in target.OrderBy(f => f).Take(10))
        {
            sb.Append("  ").Append(FuncName(thread, f)).Append("  [").Append(threadIndex).Append("/fn").Append(f).Append(']').AppendLine();
        }

        sb.Append("inclusive: ").Append(inclCount).Append(" (").Append((100.0 * inclCount / tot).ToString("0.0")).Append("%)")
          .Append("   self: ").Append(selfCount).Append(" (").Append((100.0 * selfCount / tot).ToString("0.0")).Append("%)").AppendLine();
        sb.AppendLine("top callers (who drives time into it):");
        foreach (var kv in callers.OrderByDescending(kv => kv.Value).Take(take))
        {
            sb.Append("  ").Append(kv.Value).Append(" (").Append((100.0 * kv.Value / tot).ToString("0.0")).Append("%)  ")
              .Append(FuncName(thread, kv.Key)).Append("  [").Append(threadIndex).Append("/fn").Append(kv.Key).Append(']').AppendLine();
        }

        if (callers.Count == 0)
        {
            sb.AppendLine("  (none - top of stack)");
        }

        sb.AppendLine("top callees (where it spends time):");
        foreach (var kv in callees.OrderByDescending(kv => kv.Value).Take(take))
        {
            sb.Append("  ").Append(kv.Value).Append(" (").Append((100.0 * kv.Value / tot).ToString("0.0")).Append("%)  ")
              .Append(FuncName(thread, kv.Key)).Append("  [").Append(threadIndex).Append("/fn").Append(kv.Key).Append(']').AppendLine();
        }

        if (callees.Count == 0)
        {
            sb.AppendLine("  (none - leaf)");
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "module_breakdown", ReadOnly = true, Idempotent = true)]
    [Description("Rolls up a thread self samples by module (coreclr, clrjit, managed assemblies, ...) and by category (.NET, .NET GC, .NET JIT, Native, Kernel, ...). A quick where-is-time-by-layer view. The '<none>' module row is unsymbolized native/kernel frames (no resolved module/symbol) — do not over-interpret it. Optional startMs/endMs restrict to a time window.")]
    public static string ModuleBreakdown(
        [Description("Thread index (from list_threads).")] int threadIndex,
        [Description("Absolute path to a profile .json. Optional: defaults to the most recently loaded profile.")] string? path = null,
        [Description("Max module rows (default 20).")] int? maxResults = null,
        [Description("Optional inclusive lower bound on sample time (trace-relative ms).")] double? startMs = null,
        [Description("Optional inclusive upper bound on sample time (trace-relative ms).")] double? endMs = null) => Run(() =>
    {
        int take = Math.Clamp(maxResults ?? 20, 1, MaxAllowedResults);
        var entry = ResolveProfile(path);
        var thread = ResolveThread(entry, threadIndex);
        var meta = entry.Profile.Meta;
        var stackTable = thread.StackTable;
        var frameTable = thread.FrameTable;
        var stacks = AggregateStacks(thread, startMs, endMs);

        var byModule = new Dictionary<string, long>();
        var byCategory = new Dictionary<int, long>();
        long total = stacks.Total;
        foreach (var (si, w) in stacks.ByStack)
        {
            int frame = stackTable.Frame[si];
            int func = frameTable.Func[frame];
            string mod = ModuleName(thread, func);
            if (string.IsNullOrEmpty(mod))
            {
                mod = UnsymbolizedModule;
            }

            byModule[mod] = byModule.GetValueOrDefault(mod) + w;
            int c = frameTable.Category[frame] ?? -1;
            byCategory[c] = byCategory.GetValueOrDefault(c) + w;
        }

        double tot = total > 0 ? total : 1;
        var sb = new StringBuilder();
        sb.Append("thread [").Append(threadIndex).Append("] ").Append(thread.Name)
          .Append("  selfSamples=").Append(total).Append(stacks.Suffix).AppendLine(WindowSuffix(startMs, endMs));
        sb.AppendLine("by module (self samples):");
        foreach (var kv in byModule.OrderByDescending(kv => kv.Value).Take(take))
        {
            sb.Append("  ").Append(kv.Value).Append(" (").Append((100.0 * kv.Value / tot).ToString("0.0")).Append("%)  ").Append(kv.Key);
            if (kv.Key == UnsymbolizedModule)
            {
                sb.Append("  (unsymbolized native/kernel frames)");
            }

            sb.AppendLine();
        }

        sb.AppendLine("by category (self samples):");
        foreach (var kv in byCategory.OrderByDescending(kv => kv.Value))
        {
            string cname = "<none>";
            if (kv.Key >= 0 && meta.Categories != null && kv.Key < meta.Categories.Count)
            {
                cname = meta.Categories[kv.Key].Name;
            }

            sb.Append("  ").Append(kv.Value).Append(" (").Append((100.0 * kv.Value / tot).ToString("0.0")).Append("%)  ").Append(cname).AppendLine();
        }

        return sb.ToString();
    });

    private sealed class TreeAgg
    {
        public long Incl;

        public long Self;

        public Dictionary<int, TreeAgg> Children = new();
    }
}
