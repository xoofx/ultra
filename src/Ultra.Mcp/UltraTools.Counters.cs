// Copyright (c) 2026 Kirill Osenkov. Licensed under the MIT license.
// Imported from https://github.com/KirillOsenkov/UltraMcp (commit 434b5bc); see THIRD-PARTY-NOTICES.txt.

using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Ultra.Mcp;

internal static partial class UltraTools
{
    [McpServerTool(Name = "list_counters", ReadOnly = true, Idempotent = true)]
    [Description(@"Lists the counters (memory / power / bandwidth time series) recorded in a profile, one per line: 'counterIndex name category pid mainThreadIndex samples=N'. Counters are top-level graphs separate from the per-thread sample stacks. Use counter_samples to read one counter's values over time.")]
    public static string ListCounters(
        [Description("Absolute path to a profile .json file. Optional: defaults to the most recently loaded profile.")] string? path = null) => Run(() =>
    {
        var entry = ResolveProfile(path);
        var counters = entry.Profile.Counters;

        var sb = new StringBuilder();
        if (counters == null || counters.Count == 0)
        {
            sb.AppendLine("counters: 0");
            sb.AppendLine("(this profile has no counters)");
            return sb.ToString();
        }

        sb.Append("counters: ").AppendLine(counters.Count.ToString());
        for (int i = 0; i < counters.Count; i++)
        {
            var counter = counters[i];
            sb.Append('[').Append(i).Append("] ").Append(counter.Name)
              .Append("  category=").Append(counter.Category)
              .Append("  pid=").Append(counter.Pid)
              .Append("  mainThreadIndex=").Append(counter.MainThreadIndex)
              .Append("  samples=").Append(counter.Samples.Count.Count);
            if (!string.IsNullOrEmpty(counter.Description))
            {
                sb.Append("  ").Append(counter.Description);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    });

    [McpServerTool(Name = "counter_samples", ReadOnly = true, Idempotent = true)]
    [Description(@"Enumerates one counter's samples over time, optionally restricted to a [startMs, endMs] window (trace-relative ms, same scale as query_markers), paginated. Each line is: 'sampleIndex  timeMs  count=N  [number=N]'. The header reports '(skip, take, matched)' with a trailing nextSkip=K when more results exist. Find a counterIndex with list_counters.")]
    public static string CounterSamples(
        [Description("Counter index (from list_counters).")] int counterIndex,
        [Description("Absolute path to a profile .json file. Optional: defaults to the most recently loaded profile.")] string? path = null,
        [Description("Optional inclusive lower bound on sample time (trace-relative ms).")] double? startMs = null,
        [Description("Optional inclusive upper bound on sample time (trace-relative ms).")] double? endMs = null,
        [Description("Number of leading samples (after windowing) to skip (default 0)")] int? skip = null,
        [Description("Maximum number of samples to return (default 200, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int offset = Math.Max(skip ?? 0, 0);
        int take = Math.Clamp(maxResults ?? DefaultMaxResults, 1, MaxAllowedResults);
        var entry = ResolveProfile(path);
        var counters = entry.Profile.Counters;
        if (counters == null || counters.Count == 0)
        {
            throw new McpException("This profile has no counters. Call list_counters to confirm.");
        }

        if ((uint)counterIndex >= (uint)counters.Count)
        {
            throw new McpException(
                $"counterIndex {counterIndex} is out of range. This profile has {counters.Count} counter(s). " +
                "Call list_counters to see them.");
        }

        var counter = counters[counterIndex];
        var table = counter.Samples;
        var times = ComputeCounterSampleTimes(table);

        var matches = new List<int>();
        for (int i = 0; i < times.Length; i++)
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
        sb.Append("counter [").Append(counterIndex).Append("] ").Append(counter.Name)
          .Append("  category=").Append(counter.Category).Append(WindowSuffix(startMs, endMs)).AppendLine();
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

        var number = table.Number;
        foreach (var i in page)
        {
            sb.Append(i).Append("  ").Append(times[i].ToString("0.###")).Append("ms  count=").Append(table.Count[i]);
            if (number != null && i < number.Count)
            {
                sb.Append("  number=").Append(number[i]);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    });

    // Absolute timestamp (ms, trace-relative) for each counter sample. Prefers the
    // absolute Time[] column when present; otherwise running-sums TimeDeltas[] the
    // same way ComputeSampleTimes does for thread samples.
    internal static double[] ComputeCounterSampleTimes(FirefoxProfiler.CounterSamplesTable table)
    {
        int n = table.Count.Count;
        var times = new double[n];
        if (table.Time != null)
        {
            for (int i = 0; i < n && i < table.Time.Count; i++)
            {
                times[i] = table.Time[i];
            }

            return times;
        }

        var deltas = table.TimeDeltas;
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
}
