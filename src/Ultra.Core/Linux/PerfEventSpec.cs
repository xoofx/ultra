// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Globalization;

namespace Ultra.Core.Linux;

/// <summary>
/// A hardware counter to sample (a perf event type and config), with its sampling period or frequency.
/// </summary>
/// <param name="Name">The name as given on the command line.</param>
/// <param name="Type">perf_event_attr.type.</param>
/// <param name="Config">perf_event_attr.config.</param>
/// <param name="SamplePeriodOrFrequency">The number of events between two samples, or the samples per second when <paramref name="IsFrequency"/>.</param>
/// <param name="IsFrequency">True when the kernel adapts the period to reach a sampling frequency.</param>
public sealed record PerfEventSpec(string Name, uint Type, ulong Config, ulong SamplePeriodOrFrequency, bool IsFrequency)
{
    /// <summary>The default sampling frequency of a counter (the kernel adapts the period), per CPU.</summary>
    public const ulong DefaultFrequency = 4000;

    private const uint PerfTypeHwCache = 3;

    // PERF_TYPE_HARDWARE configs
    private static readonly Dictionary<string, ulong> HardwareEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cycles"] = 0, ["cpu-cycles"] = 0,
        ["instructions"] = 1,
        ["cache-references"] = 2,
        ["cache-misses"] = 3,
        ["branches"] = 4, ["branch-instructions"] = 4,
        ["branch-misses"] = 5,
        ["bus-cycles"] = 6,
        ["stalled-cycles-frontend"] = 7,
        ["stalled-cycles-backend"] = 8,
        ["ref-cycles"] = 9,
    };

    // PERF_TYPE_HW_CACHE: cache id | op << 8 | result << 16
    private static readonly Dictionary<string, uint> Caches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["L1-dcache"] = 0, ["L1-icache"] = 1, ["LLC"] = 2, ["dTLB"] = 3, ["iTLB"] = 4, ["branch"] = 5, ["node"] = 6,
    };

    private static readonly Dictionary<string, (uint Op, uint Result)> CacheOps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["loads"] = (0, 0), ["load-misses"] = (0, 1),
        ["stores"] = (1, 0), ["store-misses"] = (1, 1),
        ["prefetches"] = (2, 0), ["prefetch-misses"] = (2, 1),
    };

    /// <summary>
    /// The known event names.
    /// </summary>
    public static IEnumerable<string> KnownNames => HardwareEvents.Keys
        .Concat(Caches.Keys.SelectMany(cache => CacheOps.Keys.Select(op => $"{cache}-{op}")))
        .Append("rHEX (raw event, e.g. r0843)");

    /// <summary>
    /// The short description of the accepted names, for the command line help.
    /// </summary>
    public static string HelpText =>
        $"generic: {string.Join(", ", HardwareEvents.Keys)}; cache: <cache>-<op> with cache = {string.Join(", ", Caches.Keys)} and op = {string.Join(", ", CacheOps.Keys)} (e.g. L1-dcache-load-misses, LLC-load-misses, dTLB-load-misses); raw: rHEX, the CPU's event code as for `perf stat -e rHEX` (e.g. r0843). `--pmc list` prints them all.";

    /// <summary>
    /// The full list of the accepted names, one per line with what they are, for <c>--pmc list</c>.
    /// </summary>
    public static string ListText
    {
        get
        {
            var lines = new List<string> { "Hardware counters for --pmc (NAME or NAME:PERIOD, comma separated; without a period the kernel adapts it to 4000 samples/s per CPU):", "", "Generic events (mapped by the kernel to the events of the CPU):" };
            lines.AddRange(HardwareEvents.GroupBy(x => x.Value).OrderBy(g => g.Key).Select(g => "  " + string.Join(", ", g.Select(x => x.Key))));
            lines.Add("");
            lines.Add("Cache events, <cache>-<op>:");
            lines.Add("  caches: " + string.Join(", ", Caches.Keys) + " (LLC = last level cache, node = local memory node)");
            lines.Add("  ops:    " + string.Join(", ", CacheOps.Keys));
            lines.Add("  e.g.    L1-dcache-load-misses, L1-icache-load-misses, LLC-load-misses, dTLB-load-misses, iTLB-load-misses, branch-load-misses");
            lines.Add("");
            lines.Add("Raw events, rHEX: the CPU's event code as for `perf stat -e rHEX` (x86: umask then event number, e.g. AMD PMCx043 umask 0x08 = r0843).");
            lines.Add("");
            lines.Add("Not every event exists on every CPU or virtual machine: an unavailable one fails when the profiling starts. `perf list` shows the events of this machine.");
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Parses a counter: <c>name</c> or <c>name:period</c>. The name is a generic hardware event (<c>cycles</c>,
    /// <c>instructions</c>, <c>cache-misses</c>, <c>branch-misses</c>...), a cache event (<c>L1-dcache-load-misses</c>,
    /// <c>LLC-load-misses</c>, <c>dTLB-load-misses</c>...), or a raw CPU event <c>rHEX</c> (the perf_event_attr.config
    /// of PERF_TYPE_RAW; on x86 the umask and event number as in <c>perf stat -e rUUEE</c>).
    /// </summary>
    /// <exception cref="ArgumentException">Unknown name or invalid period.</exception>
    public static PerfEventSpec Parse(string text) => Parse(text, DefaultFrequency);

    /// <summary>
    /// <see cref="Parse(string)"/> with the samples per second the kernel aims at when the counter has no period.
    /// </summary>
    public static PerfEventSpec Parse(string text, ulong defaultFrequency)
    {
        if (defaultFrequency == 0) throw new ArgumentException("The counter sampling frequency must be positive", nameof(defaultFrequency));
        var name = text.Trim();
        ulong period = defaultFrequency;
        bool isFrequency = true;
        int colon = name.IndexOf(':');
        if (colon >= 0)
        {
            if (!ulong.TryParse(name.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out period) || period == 0)
            {
                throw new ArgumentException($"Invalid sampling period in hardware counter `{text}`: expecting name:period");
            }
            isFrequency = false;
            name = name.Substring(0, colon);
        }

        if (name.Length == 0) throw new ArgumentException("Empty hardware counter name");

        if (HardwareEvents.TryGetValue(name, out var hwConfig))
        {
            return new PerfEventSpec(name, PerfEventNative.PERF_TYPE_HARDWARE, hwConfig, period, isFrequency);
        }

        if ((name[0] == 'r' || name[0] == 'R') && name.Length > 1 && ulong.TryParse(name.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        {
            return new PerfEventSpec(name, PerfEventNative.PERF_TYPE_RAW, raw, period, isFrequency);
        }

        foreach (var (cacheName, cacheId) in Caches)
        {
            if (!name.StartsWith(cacheName + "-", StringComparison.OrdinalIgnoreCase)) continue;
            if (CacheOps.TryGetValue(name.Substring(cacheName.Length + 1), out var op))
            {
                return new PerfEventSpec(name, PerfTypeHwCache, cacheId | (ulong)op.Op << 8 | (ulong)op.Result << 16, period, isFrequency);
            }
        }

        throw new ArgumentException($"Unknown hardware counter `{name}`. Known counters, {HelpText}");
    }
}
