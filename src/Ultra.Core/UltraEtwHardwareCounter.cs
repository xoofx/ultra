// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text;
using Microsoft.Diagnostics.Tracing.Session;

namespace Ultra.Core;

/// <summary>
/// A profile source of the machine (a hardware counter ETW can sample on), as listed by <c>wpr -pmcsources</c>.
/// </summary>
/// <param name="Name">The name of the source, e.g. <c>DcacheMisses</c>.</param>
/// <param name="Id">The id of the source, the <c>ProfileSource</c> of the PMC sample events.</param>
/// <param name="Interval">The default interval: the counter events between two samples.</param>
/// <param name="MinInterval">The smallest interval allowed.</param>
/// <param name="MaxInterval">The largest interval allowed.</param>
public readonly record struct UltraEtwProfileSource(string Name, int Id, int Interval, int MinInterval, int MaxInterval);

/// <summary>
/// A hardware counter to sample on Windows (<c>--pmc</c>): an ETW profile source and the counter events between two samples.
/// Each sample of the counter carries the call stack of the thread at that moment, so the samples weighted by the interval show
/// where the counter events happen.
/// </summary>
/// <param name="Name">The name of the profile source.</param>
/// <param name="SourceId">The id of the profile source.</param>
/// <param name="Interval">The counter events between two samples.</param>
public sealed record UltraEtwHardwareCounter(string Name, int SourceId, int Interval)
{
    /// <summary>
    /// The id of the timer source, the CPU sampling itself.
    /// </summary>
    public const int TimerSourceId = 0;

    /// <summary>
    /// The default interval of the counters that count at about the clock rate (cycles, instructions, accesses, fetches):
    /// about 5,000 samples per second per busy core at 5 GHz. The OS default (65,536) would give 76,000.
    /// </summary>
    public const int HighRateDefaultInterval = 1_000_000;

    /// <summary>
    /// The Linux names accepted on Windows, with the profile sources they stand for (the first one the machine has).
    /// </summary>
    private static readonly (string Alias, string[] Sources)[] Aliases =
    [
        ("cycles", ["TotalCycles"]),
        ("cpu-cycles", ["TotalCycles"]),
        ("instructions", ["InstructionRetired", "FRRetiredx86Instructions", "TotalIssues"]),
        ("branches", ["BranchInstructions", "FRRetiredBranches"]),
        ("branch-instructions", ["BranchInstructions", "FRRetiredBranches"]),
        ("branch-misses", ["BranchMispredictions", "FRRetiredBranchesMispredicted"]),
        ("cache-misses", ["CacheMisses"]),
        ("L1-dcache-load-misses", ["DcacheMisses"]),
        ("L1-dcache-loads", ["DcacheAccesses", "DCAccess"]),
        ("L1-icache-load-misses", ["IcacheMisses", "ICMiss"]),
    ];

    /// <summary>
    /// Gets the profile sources of this machine, the timer included (empty if they cannot be read).
    /// </summary>
    public static List<UltraEtwProfileSource> GetSources()
    {
        try
        {
            return TraceEventProfileSources.GetInfo().Values
                .Select(x => new UltraEtwProfileSource(x.Name, x.ID, x.Interval, x.MinInterval, x.MaxInterval))
                .OrderBy(x => x.Id)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// The default interval of the miss counters (rare events), the Windows default.
    /// </summary>
    public const int MissDefaultInterval = 65_536;

    /// <summary>
    /// Gets the default interval of a source: <see cref="MissDefaultInterval"/> for the miss counters (rare events),
    /// <see cref="HighRateDefaultInterval"/> for the counters that count at about the clock rate. Fixed numbers: the interval
    /// the machine reports is the last one set by any tool (the setting is system wide).
    /// </summary>
    public static int GetDefaultInterval(UltraEtwProfileSource source)
    {
        var rare = source.Name.Contains("Miss", StringComparison.OrdinalIgnoreCase) || source.Name.Contains("Mispredict", StringComparison.OrdinalIgnoreCase);
        return Math.Clamp(rare ? MissDefaultInterval : HighRateDefaultInterval, source.MinInterval, source.MaxInterval);
    }

    /// <summary>
    /// Parses a counter <c>NAME[:INTERVAL]</c>: the name of a profile source of the machine (any case) or a Linux name
    /// (<c>cycles</c>, <c>cache-misses</c>, <c>L1-dcache-load-misses</c>, ...), and the counter events between two samples.
    /// </summary>
    /// <param name="text">The counter text.</param>
    /// <param name="sources">The profile sources of the machine, see <see cref="GetSources"/>.</param>
    /// <exception cref="ArgumentException">The name is unknown, the timer, or the interval is not a number in the range of the source.</exception>
    public static UltraEtwHardwareCounter Parse(string text, IReadOnlyList<UltraEtwProfileSource> sources)
    {
        var colon = text.IndexOf(':');
        var name = (colon >= 0 ? text[..colon] : text).Trim();
        var intervalText = colon >= 0 ? text[(colon + 1)..].Trim() : null;

        var source = Find(name, sources)
                     ?? throw new ArgumentException(sources.Count == 0
                         ? $"Unknown hardware counter `{name}`: this machine lists no profile sources (`wpr -pmcsources`)"
                         : $"Unknown hardware counter `{name}`. Counters of this machine: {string.Join(", ", sources.Where(x => x.Id != TimerSourceId).Select(x => x.Name))}");

        if (source.Id == TimerSourceId)
        {
            throw new ArgumentException($"`{source.Name}` is the CPU sampling itself, set its rate with --sampling-interval");
        }

        var interval = GetDefaultInterval(source);
        if (intervalText is not null)
        {
            if (!int.TryParse(intervalText, out interval) || interval < source.MinInterval || interval > source.MaxInterval)
            {
                throw new ArgumentException($"Invalid interval `{intervalText}` for `{source.Name}`: a number of events from {source.MinInterval} to {source.MaxInterval}");
            }
        }

        return new UltraEtwHardwareCounter(source.Name, source.Id, interval);
    }

    /// <summary>
    /// Parses a list of counters, rejecting a source given twice.
    /// </summary>
    public static List<UltraEtwHardwareCounter> ParseAll(IEnumerable<string> texts, IReadOnlyList<UltraEtwProfileSource> sources)
    {
        var counters = new List<UltraEtwHardwareCounter>();
        foreach (var text in texts)
        {
            var counter = Parse(text, sources);
            if (counters.Any(x => x.SourceId == counter.SourceId))
            {
                throw new ArgumentException($"The hardware counter `{counter.Name}` is given twice");
            }
            counters.Add(counter);
        }
        return counters;
    }

    private static UltraEtwProfileSource? Find(string name, IReadOnlyList<UltraEtwProfileSource> sources)
    {
        foreach (var source in sources)
        {
            if (source.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        foreach (var (alias, names) in Aliases)
        {
            if (!alias.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var sourceName in names)
            {
                foreach (var source in sources)
                {
                    if (source.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        return source;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The text of <c>--pmc list</c> on Windows: the profile sources of this machine with their default intervals, and the Linux names accepted.
    /// </summary>
    public static string ListText => FormatList(GetSources());

    internal static string FormatList(IReadOnlyList<UltraEtwProfileSource> sources)
    {
        var builder = new StringBuilder();
        var counters = sources.Where(x => x.Id != TimerSourceId).ToList();
        if (counters.Count == 0)
        {
            builder.AppendLine("This machine lists no hardware counters (`wpr -pmcsources`): a VM without a virtual PMU, or the counters are not exposed.");
            return builder.ToString();
        }

        builder.AppendLine("Hardware counters of this machine (ETW profile sources), --pmc NAME[:INTERVAL], INTERVAL = counter events between two samples:");
        builder.AppendLine();
        builder.AppendLine($"  {"Name",-32} {"Default",10} {"Min",8} {"Max",11}");
        foreach (var source in counters)
        {
            builder.AppendLine($"  {source.Name,-32} {GetDefaultInterval(source),10} {source.MinInterval,8} {source.MaxInterval,11}");
        }
        builder.AppendLine();

        var aliases = Aliases.Select(x => (x.Alias, Source: Find(x.Alias, sources))).Where(x => x.Source is not null).ToList();
        if (aliases.Count > 0)
        {
            builder.AppendLine("Linux names accepted: " + string.Join(", ", aliases.Select(x => $"{x.Alias} = {x.Source!.Value.Name}")));
        }
        builder.AppendLine("The CPU counts a few sources at once (AMD and Intel cores: 4 to 6); the session fails to start with more.");
        builder.AppendLine("Counters not listed here (L2, TLB, ...) need a vendor tool (AMD uProf, Intel VTune) or Linux.");
        return builder.ToString();
    }
}
