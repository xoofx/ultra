// Copyright (c) 2026 Kirill Osenkov. Licensed under the MIT license.
// Imported from https://github.com/KirillOsenkov/UltraMcp (commit 434b5bc); see THIRD-PARTY-NOTICES.txt.

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace Ultra.Mcp;

internal static partial class UltraTools
{
    [McpServerTool(Name = "query_markers", ReadOnly = true, Idempotent = true)]
    [Description(@"Lists markers (JIT compiles, GC events, allocations, etc.) for one thread, optionally filtered by a case-insensitive name substring, paginated. Each line is: 'startMs..endMs  name  [threadIndex/mk<markerIndex>]'.

The header reports '(skip, take, matched)' with a trailing '+' or nextSkip=K when more results exist beyond the page.")]
    public static string QueryMarkers(
        [Description("Thread index (from list_threads)")] int threadIndex,
        [Description("Absolute path to a profile .json file. Optional: defaults to the most recently loaded profile.")] string? path = null,
        [Description("Optional case-insensitive substring matched against the marker name")] string? nameContains = null,
        [Description("Number of leading results to skip (default 0)")] int? skip = null,
        [Description("Maximum number of markers to return (default 200, max 5000)")] int? maxResults = null) => Run(() =>
    {
        int offset = Math.Max(skip ?? 0, 0);
        int take = Math.Clamp(maxResults ?? DefaultMaxResults, 1, MaxAllowedResults);
        var entry = ResolveProfile(path);
        var thread = ResolveThread(entry, threadIndex);
        var markers = thread.Markers;

        string? filter = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains.Trim();

        var matches = new List<int>();
        for (int i = 0; i < markers.Length; i++)
        {
            string name = SafeString(thread, markers.Name[i]);
            if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            matches.Add(i);
        }

        int total = matches.Count;
        var page = matches.Skip(offset).Take(take).ToList();

        var sb = new StringBuilder();
        sb.Append("markers: ").Append(page.Count)
          .Append(" (skip=").Append(offset)
          .Append(", take=").Append(take)
          .Append(", matched=").Append(total);
        if (offset + page.Count < total)
        {
            sb.Append(", nextSkip=").Append(offset + page.Count);
        }

        sb.AppendLine(")");

        if (page.Count == 0)
        {
            sb.AppendLine("(no markers match)");
            return sb.ToString();
        }

        foreach (var i in page)
        {
            string name = SafeString(thread, markers.Name[i]);
            double startMs = markers.StartTime[i] ?? 0;
            double? endMs = markers.EndTime[i];
            sb.Append(startMs.ToString("0.###")).Append("..");
            sb.Append(endMs.HasValue ? endMs.Value.ToString("0.###") : "-");
            sb.Append("  ").Append(name)
              .Append("  [").Append(threadIndex).Append("/mk").Append(i).Append(']')
              .AppendLine();
        }

        return sb.ToString();
    });
}
