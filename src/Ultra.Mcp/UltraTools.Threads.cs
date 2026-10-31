// Copyright (c) 2026 Kirill Osenkov. Licensed under the MIT license.
// Imported from https://github.com/KirillOsenkov/UltraMcp (commit 434b5bc); see THIRD-PARTY-NOTICES.txt.

using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Ultra.Mcp;

internal static partial class UltraTools
{
    [McpServerTool(Name = "list_threads", ReadOnly = true, Idempotent = true)]
    [Description(@"Lists all threads in a profile, one per line: 'threadIndex name samples=N markers=M pid=P tid=T'. threadIndex is the canonical handle used by every other thread-scoped tool. Sorted by descending sample count so the hottest threads are first.")]
    public static string ListThreads(
        [Description("Absolute path to a profile .json file. Optional: defaults to the most recently loaded profile.")] string? path = null) => Run(() =>
    {
        var entry = ResolveProfile(path);
        var threads = entry.Profile.Threads;

        var rows = new List<(int Index, FirefoxProfiler.Thread Thread)>();
        for (int i = 0; i < threads.Count; i++)
        {
            rows.Add((i, threads[i]));
        }

        rows.Sort((a, b) => b.Thread.Samples.Length.CompareTo(a.Thread.Samples.Length));

        var sb = new StringBuilder();
        sb.Append("threads: ").AppendLine(threads.Count.ToString());
        foreach (var (index, thread) in rows)
        {
            sb.Append('[').Append(index).Append("] ")
              .Append(thread.Name)
              .Append("  samples=").Append(thread.Samples.Length)
              .Append(" markers=").Append(thread.Markers.Length)
              .Append(" pid=").Append(thread.Pid)
              .Append(" tid=").Append(thread.Tid)
              .AppendLine();
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "top_functions", ReadOnly = true, Idempotent = true)]
    [Description(@"Aggregates the hottest functions in one thread by self-sample count (the leaf frame of each sample), the classic 'where is time spent?' view. Each line is: 'selfSamples (pct%)  module!func  [threadIndex/fn<funcIndex>]'. Sorted by descending self samples.

Self samples counts a function only when it is the innermost frame actively executing — use get_call_stack to see the surrounding stack of any sample. For inclusive time (expensive subtrees) use call_tree. Optional startMs/endMs restrict to a time window (trace-relative ms, same scale as query_markers).")]
    public static string TopFunctions(
        [Description("Thread index (from list_threads)")] int threadIndex,
        [Description("Absolute path to a profile .json file. Optional: defaults to the most recently loaded profile.")] string? path = null,
        [Description("Maximum number of functions to return (default 200, max 5000)")] int? maxResults = null,
        [Description("Optional inclusive lower bound on sample time (trace-relative ms).")] double? startMs = null,
        [Description("Optional inclusive upper bound on sample time (trace-relative ms).")] double? endMs = null) => Run(() =>
    {
        int take = Math.Clamp(maxResults ?? DefaultMaxResults, 1, MaxAllowedResults);
        var entry = ResolveProfile(path);
        var thread = ResolveThread(entry, threadIndex);

        var stackTable = thread.StackTable;
        var frameTable = thread.FrameTable;
        var samples = thread.Samples;
        double[]? times = MaybeSampleTimes(thread, startMs, endMs);

        var counts = new Dictionary<int, int>();
        int total = 0;
        for (int i = 0; i < samples.Stack.Count; i++)
        {
            if (samples.Stack[i] is int stackIndex && stackIndex >= 0 && stackIndex < stackTable.Length)
            {
                if (times != null && !InWindow(times[i], startMs, endMs))
                {
                    continue;
                }

                int frame = stackTable.Frame[stackIndex];
                int func = frameTable.Func[frame];
                counts[func] = counts.GetValueOrDefault(func) + 1;
                total++;
            }
        }

        var ordered = counts.OrderByDescending(kv => kv.Value).Take(take).ToList();

        var sb = new StringBuilder();
        sb.Append("thread [").Append(threadIndex).Append("] ").Append(thread.Name)
          .Append("  functions=").Append(counts.Count)
          .Append(" totalSelfSamples=").Append(total)
          .AppendLine(WindowSuffix(startMs, endMs));
        foreach (var (func, count) in ordered)
        {
            double pct = total > 0 ? 100.0 * count / total : 0;
            sb.Append(count).Append(" (").Append(pct.ToString("0.0")).Append("%)  ")
              .Append(FuncName(thread, func))
              .Append("  [").Append(threadIndex).Append("/fn").Append(func).Append(']')
              .AppendLine();
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "list_samples", ReadOnly = true, Idempotent = true)]
    [Description(@"Enumerates individual samples in one thread, optionally restricted to a [startMs, endMs] time window (trace-relative ms, same scale as query_markers), paginated. Each line is: 'sampleIndex  timeMs  stackIndex  module!leafFunc  [threadIndex/fn<funcIndex>]'.

Use this to locate concrete samples inside a spike or interaction you spotted in query_markers, then feed a sampleIndex to get_call_stack (or get_sample) to see the full stack. The header reports '(skip, take, matched)' with a trailing nextSkip=K when more results exist beyond the page.")]
    public static string ListSamples(
        [Description("Thread index (from list_threads)")] int threadIndex,
        [Description("Absolute path to a profile .json file. Optional: defaults to the most recently loaded profile.")] string? path = null,
        [Description("Optional inclusive lower bound on sample time (trace-relative ms).")] double? startMs = null,
        [Description("Optional inclusive upper bound on sample time (trace-relative ms).")] double? endMs = null,
        [Description("Number of leading samples (after windowing) to skip (default 0)")] int? skip = null,
        [Description("Maximum number of samples to return (default 200, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int offset = Math.Max(skip ?? 0, 0);
        int take = Math.Clamp(maxResults ?? DefaultMaxResults, 1, MaxAllowedResults);
        var entry = ResolveProfile(path);
        var thread = ResolveThread(entry, threadIndex);
        var samples = thread.Samples;
        var stackTable = thread.StackTable;
        var frameTable = thread.FrameTable;
        var times = ComputeSampleTimes(thread);

        var matches = new List<int>();
        for (int i = 0; i < samples.Stack.Count; i++)
        {
            if (!InWindow(times[i], startMs, endMs))
            {
                continue;
            }

            matches.Add(i);
        }

        int totalMatched = matches.Count;
        var page = matches.Skip(offset).Take(take).ToList();

        var sb = new StringBuilder();
        sb.Append("thread [").Append(threadIndex).Append("] ").Append(thread.Name).Append(WindowSuffix(startMs, endMs)).AppendLine();
        sb.Append("samples: ").Append(page.Count)
          .Append(" (skip=").Append(offset)
          .Append(", take=").Append(take)
          .Append(", matched=").Append(totalMatched);
        if (offset + page.Count < totalMatched)
        {
            sb.Append(", nextSkip=").Append(offset + page.Count);
        }

        sb.AppendLine(")");

        if (page.Count == 0)
        {
            sb.AppendLine("(no samples in window)");
            return sb.ToString();
        }

        foreach (var i in page)
        {
            sb.Append(i).Append("  ").Append(times[i].ToString("0.###")).Append("ms  ");
            if (samples.Stack[i] is int si && si >= 0 && si < stackTable.Length)
            {
                int frame = stackTable.Frame[si];
                int func = frameTable.Func[frame];
                sb.Append("stack=").Append(si).Append("  ").Append(FuncName(thread, func))
                  .Append("  [").Append(threadIndex).Append("/fn").Append(func).Append(']');
            }
            else
            {
                sb.Append("stack=null");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "get_sample", ReadOnly = true, Idempotent = true)]
    [Description(@"Returns one sample's timestamp and full call stack (leaf first), resolved from its threadIndex + sampleIndex. Convenience wrapper: locate a sample with list_samples (in a time window), then inspect it here. Each stack line: 'module!func [threadIndex/fn<funcIndex>]'.")]
    public static string GetSample(
        [Description("Thread index (from list_threads)")] int threadIndex,
        [Description("Sample index (from list_samples)")] int sampleIndex,
        [Description("Absolute path to a profile .json file. Optional: defaults to the most recently loaded profile.")] string? path = null) => Run(() =>
    {
        var entry = ResolveProfile(path);
        var thread = ResolveThread(entry, threadIndex);
        var samples = thread.Samples;
        var stackTable = thread.StackTable;
        var frameTable = thread.FrameTable;

        if ((uint)sampleIndex >= (uint)samples.Stack.Count)
        {
            throw new McpException($"sampleIndex {sampleIndex} is out of range (0..{samples.Stack.Count - 1}).");
        }

        var times = ComputeSampleTimes(thread);
        var sb = new StringBuilder();
        sb.Append("thread [").Append(threadIndex).Append("] ").Append(thread.Name).AppendLine();
        sb.Append("sampleIndex: ").Append(sampleIndex).AppendLine();
        sb.Append("timeMs: ").Append(times[sampleIndex].ToString("0.###")).AppendLine();

        if (samples.Stack[sampleIndex] is not int start || start < 0 || start >= stackTable.Length)
        {
            sb.AppendLine("stack: (none)");
            return sb.ToString();
        }

        var frames = new List<int>();
        int? current = start;
        int guard = 0;
        while (current is int row && guard++ < 100_000)
        {
            frames.Add(stackTable.Frame[row]);
            current = stackTable.Prefix[row];
        }

        sb.Append("stackIndex: ").Append(start).Append("  depth=").Append(frames.Count).AppendLine();
        for (int depth = 0; depth < frames.Count; depth++)
        {
            int func = frameTable.Func[frames[depth]];
            sb.Append(' ', depth * 2)
              .Append(FuncName(thread, func))
              .Append("  [").Append(threadIndex).Append("/fn").Append(func).Append(']')
              .AppendLine();
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "get_call_stack", ReadOnly = true, Idempotent = true)]
    [Description(@"Resolves one stackTable row into a full call stack by walking the prefix links up to the root. Each line is one frame, indented by depth, leaf first: 'module!func [threadIndex/fn<funcIndex>]'.

Find a stackIndex or sampleIndex with list_samples (enumerate samples in a time window), or pass a sampleIndex directly.")]
    public static string GetCallStack(
        [Description("Thread index (from list_threads)")] int threadIndex,
        [Description("A stackTable row index. Provide this OR sampleIndex.")] int? stackIndex = null,
        [Description("A sample index; its stack is resolved. Provide this OR stackIndex.")] int? sampleIndex = null,
        [Description("Absolute path to a profile .json file. Optional: defaults to the most recently loaded profile.")] string? path = null) => Run(() =>
    {
        var entry = ResolveProfile(path);
        var thread = ResolveThread(entry, threadIndex);
        var stackTable = thread.StackTable;
        var frameTable = thread.FrameTable;

        int start;
        if (stackIndex.HasValue && sampleIndex.HasValue)
        {
            throw new McpException("Provide either stackIndex or sampleIndex, not both.");
        }
        else if (stackIndex.HasValue)
        {
            start = stackIndex.Value;
        }
        else if (sampleIndex.HasValue)
        {
            var samples = thread.Samples;
            if ((uint)sampleIndex.Value >= (uint)samples.Stack.Count)
            {
                throw new McpException($"sampleIndex {sampleIndex.Value} is out of range (0..{samples.Stack.Count - 1}).");
            }

            if (samples.Stack[sampleIndex.Value] is not int s)
            {
                return $"sample {sampleIndex.Value} has no stack (null).";
            }

            start = s;
        }
        else
        {
            throw new McpException("Provide either stackIndex or sampleIndex.");
        }

        if ((uint)start >= (uint)stackTable.Length)
        {
            throw new McpException($"stackIndex {start} is out of range (0..{stackTable.Length - 1}).");
        }

        var frames = new List<int>();
        int? current = start;
        int guard = 0;
        while (current is int row && guard++ < 100_000)
        {
            frames.Add(stackTable.Frame[row]);
            current = stackTable.Prefix[row];
        }

        var sb = new StringBuilder();
        sb.Append("thread [").Append(threadIndex).Append("] ").Append(thread.Name)
          .Append("  depth=").Append(frames.Count).AppendLine();
        for (int depth = 0; depth < frames.Count; depth++)
        {
            int frame = frames[depth];
            int func = frameTable.Func[frame];
            sb.Append(' ', depth * 2)
              .Append(FuncName(thread, func))
              .Append("  [").Append(threadIndex).Append("/fn").Append(func).Append(']')
              .AppendLine();
        }

        return sb.ToString();
    });
}
