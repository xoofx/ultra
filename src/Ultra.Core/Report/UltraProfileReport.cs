// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Ultra.Core.Report;

/// <summary>
/// A text report of a Firefox profile written by ultra, without loading it whole: the CPU time per thread and its share, the top
/// leaf functions over all threads, the hot code offsets of the top functions, the events of each hardware counter track per
/// function, and the .NET GC: the collections and runtime suspensions, the GC heap, and the sampled allocations per thread and
/// type (from the GC markers of the threads). The <c>[counter]</c> tracks of <c>--pmc</c> are reported apart from the CPU threads
/// (a sum over all threads would count them as CPU).
/// </summary>
/// <remarks>
/// The profile is streamed thread by thread: one thread inflates the gzip and cuts the JSON into complete thread objects, workers
/// parse them with <see cref="Utf8JsonReader"/> into pooled arrays (no DOM) and tally them, the tallies are merged at the end. A
/// 150 MB trace of a 2 minute run on 12 cores takes a few seconds, bound by the inflate. With <see cref="Detailed"/>, each CPU thread
/// also keeps its samples per call path of functions (a few MB for a large trace), from which the call trees are built at the end.
/// </remarks>
public sealed partial class UltraProfileReport
{
    /// <summary>
    /// Creates a report.
    /// </summary>
    /// <param name="top">How many threads and functions to list.</param>
    /// <param name="codeOffsets">Also the hot code offsets of the top 3 functions (the frame addresses: offsets in the function's code).</param>
    /// <param name="threadFilter">Only the threads whose name contains this, when given.</param>
    /// <param name="workers">The parsing threads (default: half the processors).</param>
    public UltraProfileReport(int top = 12, bool codeOffsets = false, string? threadFilter = null, int workers = 0)
        : this(new UltraProfileReportOptions { Top = top, CodeOffsets = codeOffsets, ThreadFilter = threadFilter, Workers = workers })
    {
    }

    /// <summary>
    /// Creates a report with all its options: the compact text, the call trees and the time window.
    /// </summary>
    public UltraProfileReport(UltraProfileReportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Top = Math.Max(1, options.Top);
        CodeOffsets = options.CodeOffsets;
        ThreadFilter = options.ThreadFilter;
        Workers = options.Workers > 0 ? options.Workers : Math.Max(1, Environment.ProcessorCount / 2);
        Compact = options.Compact;
        Focus = string.IsNullOrWhiteSpace(options.Focus) ? null : options.Focus.Trim();
        Detailed = options.Detailed || Focus != null;
        TreeThreads = Math.Max(0, options.TreeThreads);
        Hotspots = Math.Max(0, options.Hotspots);
        CollapseRecursion = options.CollapseRecursion;
        MaxDepth = Math.Clamp(options.MaxDepth, 1, 200);
        MinPercent = double.IsFinite(options.MinPercent) ? Math.Clamp(options.MinPercent, 0, 100) : 1.0;
        MaxRows = Math.Max(1, options.MaxRows);
        if (options.StartMs.HasValue || options.EndMs.HasValue)
        {
            Window = new TimeWindow(options.StartMs ?? double.NegativeInfinity, options.EndMs ?? double.PositiveInfinity);
        }
    }

    /// <summary>How many threads and functions to list.</summary>
    public int Top { get; }

    /// <summary>Whether the hot code offsets of the top functions are listed.</summary>
    public bool CodeOffsets { get; }

    /// <summary>Only the threads whose name contains this, when not null.</summary>
    public string? ThreadFilter { get; }

    /// <summary>The parsing threads.</summary>
    public int Workers { get; }

    /// <summary>Whether the report is the compact text for agents (see <see cref="UltraProfileReportOptions.Compact"/>).</summary>
    public bool Compact { get; }

    /// <summary>Whether the report ends with the call trees (also when <see cref="Focus"/> is given).</summary>
    public bool Detailed { get; }

    /// <summary>How many of the busiest threads get a top-down tree.</summary>
    public int TreeThreads { get; }

    /// <summary>How many of the top functions by self samples get a bottom-up tree.</summary>
    public int Hotspots { get; }

    /// <summary>The functions whose name contains this get the trees instead of the top functions, when not null.</summary>
    public string? Focus { get; }

    /// <summary>Whether the trees collapse the recursion (see <see cref="UltraProfileReportOptions.CollapseRecursion"/>).</summary>
    public bool CollapseRecursion { get; }

    /// <summary>The rows from the root to the deepest row of a tree.</summary>
    public int MaxDepth { get; }

    /// <summary>The rows under this percent of their tree are left out.</summary>
    public double MinPercent { get; }

    /// <summary>The rows of a tree at most.</summary>
    public int MaxRows { get; }

    /// <summary>The samples and markers of the report: from start to end (ms from the start of the profile, both included), all when null.</summary>
    private TimeWindow? Window { get; }

    /// <summary>A time window, its bounds included, as the MCP tools' startMs/endMs. NaN is outside.</summary>
    private readonly record struct TimeWindow(double Start, double End)
    {
        public bool Contains(double time) => time >= Start && time <= End;

        public override string ToString()
            => FormattableString.Invariant($"[{(double.IsNegativeInfinity(Start) ? "start" : Start.ToString("0.###", CultureInfo.InvariantCulture))}..{(double.IsPositiveInfinity(End) ? "end" : End.ToString("0.###", CultureInfo.InvariantCulture))}]ms");
    }

    /// <summary>The time the report covers: the profile, or the part of it in the window.</summary>
    private double DurationMs(ProfileMeta meta)
    {
        if (Window is not { } window) return meta.DurationMs;
        var end = Math.Min(window.End, meta.DurationMs > 0 ? meta.DurationMs : window.End);
        var start = Math.Max(window.Start, 0);
        return double.IsFinite(end) && end > start ? end - start : 0;
    }

    /// <summary>
    /// Writes the report of the profile file (<c>.json.gz</c> or <c>.json</c>) to <paramref name="output"/>.
    /// </summary>
    public void Run(string path, TextWriter output)
    {
        using var file = File.OpenRead(path);
        using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        Run(input, output);
    }

    /// <summary>
    /// Writes the report of a profile (the JSON, already inflated) to <paramref name="output"/>.
    /// </summary>
    public void Run(Stream json, TextWriter output)
    {
        var meta = new ProfileMeta();
        var slices = Channel.CreateBounded<ThreadSlice>(new BoundedChannelOptions(Workers * 2) { SingleWriter = true });
        var tallies = new Tally[Workers];
        var tasks = new Task[Workers];
        var names = Detailed ? new NameTable() : null;
        for (int w = 0; w < Workers; w++)
        {
            var tally = tallies[w] = new Tally(this, names);
            tasks[w] = Task.Run(async () =>
            {
                await foreach (var slice in slices.Reader.ReadAllAsync())
                {
                    try { ThreadParser.Parse(slice.Bytes.AsSpan(0, slice.Length), tally); }
                    finally { ArrayPool<byte>.Shared.Return(slice.Bytes); }
                }
            });
        }

        try
        {
            new ProfileStreamer(json).Run(meta, slice => slices.Writer.WriteAsync(slice).AsTask().GetAwaiter().GetResult());
        }
        finally
        {
            slices.Writer.Complete();
            Task.WaitAll(tasks);
        }

        Print(meta, tallies, output);
    }

    /// <summary>The tallies of the workers merged: what both texts print.</summary>
    private sealed record Totals(
        List<(string Name, int Samples, double CpuMs)> CpuThreads,
        Dictionary<string, long> CpuLeaf,
        Dictionary<string, (int Tracks, long Samples, long Events)> Counters,
        int Threads,
        double TotalCpuMs)
    {
        /// <summary>The CPU threads, the busiest first.</summary>
        public List<(string Name, int Samples, double CpuMs)> ThreadsByCpu { get; } =
            CpuThreads.OrderByDescending(x => x.CpuMs).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
    }

    private void Print(ProfileMeta meta, Tally[] tallies, TextWriter o)
    {
        var cpuThreads = tallies.SelectMany(t => t.CpuThreads).ToList();
        var cpuLeaf = Merge(tallies.Select(t => t.CpuLeaf));
        var counters = new Dictionary<string, (int Tracks, long Samples, long Events)>();
        foreach (var t in tallies)
        {
            foreach (var (k, v) in t.Counters)
            {
                var c = counters.GetValueOrDefault(k);
                counters[k] = (c.Tracks + v.Tracks, c.Samples + v.Samples, c.Events + v.Events);
            }
        }
        var totals = new Totals(cpuThreads, cpuLeaf, counters, tallies.Sum(t => t.Threads), cpuThreads.Sum(x => x.CpuMs));

        if (Compact)
        {
            BuildShortNames(tallies);
            PrintCompact(meta, totals, tallies, o);
        }
        else
        {
            PrintText(meta, totals, tallies, o);
        }

        if (Detailed) PrintTrees(totals, tallies, o);
    }

    private void PrintText(ProfileMeta meta, Totals totals, Tally[] tallies, TextWriter o)
    {
        var (cpuThreads, cpuLeaf, counters, threads, totalCpu) = totals;
        var duration = DurationMs(meta);

        // The report is read by tools and agents (`ultra report`, MCP): the same numbers whatever the culture of the machine
        void Line(FormattableString text) => o.WriteLine(text.ToString(CultureInfo.InvariantCulture));

        Line($"product={meta.Product} abi={meta.Abi} cpus={meta.Cpus} duration={meta.DurationMs / 1000:F1} s{(meta.IntervalMs > 0 ? $" sampling={1000 / meta.IntervalMs:F0}/s" : "")}");
        if (Window is { } window) Line($"window={window} ({duration / 1000:F1} s): only the samples and markers in it, the GC heap snapshot is whole");
        o.WriteLine();
        var counterTracks = string.Join(", ", counters.OrderBy(x => x.Key).Select(x => FormattableString.Invariant($"{x.Key}: {x.Value.Tracks}")));
        Line($"{threads} threads in the file{(ThreadFilter != null ? FormattableString.Invariant($", those whose name contains \"{ThreadFilter}\"") : "")}: {cpuThreads.Count} CPU threads and {counters.Values.Sum(x => x.Tracks)} counter tracks{(counters.Count > 0 ? $" ({counterTracks})" : "")}");
        Line($"CPU: {totalCpu / 1000:F1} s over {duration / 1000:F1} s = {totalCpu / Math.Max(1, duration):F2} cores busy on average, {cpuThreads.Sum(x => (long)x.Samples):N0} samples");
        o.WriteLine("per thread (samples, CPU ms, share of the process CPU):");
        var rows = totals.ThreadsByCpu;
        foreach (var (name, n, cpu) in rows.Take(Top))
        {
            Line($"  {name,-44} {n,9:N0} {cpu,10:N0} ms {(totalCpu > 0 ? cpu / totalCpu * 100 : 0),5:F1}%");
        }
        if (rows.Count > Top)
        {
            var rest = rows.Skip(Top).Sum(x => x.CpuMs);
            Line($"  ... {rows.Count - Top} more threads {rest,24:N0} ms {(totalCpu > 0 ? rest / totalCpu * 100 : 0),5:F1}%");
        }

        var n2 = cpuLeaf.Values.Sum();
        o.WriteLine();
        Line($"top leaf functions over all CPU threads ({n2:N0} samples):");
        foreach (var (leaf, c) in cpuLeaf.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(Top))
        {
            Line($"  {c,10:N0} {c * 100.0 / Math.Max(1, n2),5:F1}%  {leaf}");
        }

        if (CodeOffsets)
        {
            foreach (var (leaf, _) in cpuLeaf.OrderByDescending(x => x.Value).Take(3))
            {
                var (offsets, tiers) = CodeOffsetsOf(tallies, leaf);
                o.WriteLine();
                if (offsets.Count == 0)
                {
                    // said, not skipped: an empty list is the trace's (no address recorded for its frames), not the report's
                    Line($"hot code offsets in {leaf}: none, its frames have no code address in the trace");
                    continue;
                }
                var total = offsets.Sum(x => x.Samples);
                var leafSamples = tiers.Sum(x => x.Samples);
                var tierText = tiers.Count > 0 ? "; code versions: " + string.Join(", ", tiers.Select(x => FormattableString.Invariant($"{TierLabel(x.Tier)} {x.Samples * 100.0 / leafSamples:F1}%"))) : "";
                Line($"hot code offsets in {leaf} ({total:N0} samples with an address, {offsets.Count} distinct{tierText}):");
                foreach (var (tier, offset, c) in offsets.Take(Top))
                {
                    Line($"  +0x{(uint)offset:x}{(tiers.Count > 0 ? " " + TierLabel(tier) : "")}: {c:N0} ({c * 100.0 / total:F1}%)");
                }
            }
        }

        foreach (var (counter, (tracks, samples, events)) in counters.OrderBy(x => x.Key))
        {
            var leafs = Merge(tallies.Select(t => t.CounterLeaf.GetValueOrDefault(counter)).Where(x => x != null)!);
            o.WriteLine();
            Line($"counter [{counter}]: {tracks} tracks, {samples:N0} samples standing for {events:N0} events; top leaf functions by events:");
            foreach (var (leaf, c) in leafs.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(Top))
            {
                Line($"  {c,16:N0} {(events > 0 ? c * 100.0 / events : 0),5:F1}%  {leaf}");
            }
        }

        PrintGc(meta, GcTally.Merge(tallies.Select(t => t.Gc)), o);
        PrintLocks(LockTally.Merge(tallies.Select(t => t.Locks)), o);
        PrintHeap(HeapTally.Merge(tallies.Select(t => t.Heap)), o);
    }

    /// <summary>
    /// The code offsets of a leaf function, the most sampled first, each with its code version (the optimization tier of the code
    /// it is an offset in: Tier0, Tier1, OSR...), and the samples of the function per code version. No versions when the trace has
    /// none (native code, older conversions, Linux): the offsets are then printed as before, without a version column.
    /// </summary>
    private static (List<(string Tier, int Offset, long Samples)> Offsets, List<(string Tier, long Samples)> Tiers) CodeOffsetsOf(Tally[] tallies, string leaf)
    {
        var byOffset = Merge(tallies.Select(t => t.CpuOffsets.GetValueOrDefault(leaf)).Where(x => x != null)!);
        var byTier = Merge(tallies.Select(t => t.CpuTiers.GetValueOrDefault(leaf)).Where(x => x != null)!);
        var offsets = byOffset.OrderByDescending(x => x.Value).ThenBy(x => x.Key.Tier, StringComparer.Ordinal).ThenBy(x => (uint)x.Key.Offset)
            .Select(x => (x.Key.Tier, x.Key.Offset, x.Value)).ToList();
        var tiers = byTier.Keys.Any(x => x.Length > 0)
            ? byTier.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => (x.Key, x.Value)).ToList()
            : [];
        return (offsets, tiers);
    }

    /// <summary>A code version as printed: its tier, "-" for the frames without one (native code beside the JIT'd versions).</summary>
    private static string TierLabel(string tier) => tier.Length > 0 ? tier : "-";

    private static Dictionary<TKey, long> Merge<TKey>(IEnumerable<Dictionary<TKey, long>> parts) where TKey : notnull
    {
        var result = new Dictionary<TKey, long>();
        foreach (var part in parts)
        {
            foreach (var (k, v) in part) result[k] = result.GetValueOrDefault(k) + v;
        }
        return result;
    }

    /// <summary>A complete thread object cut out of the profile: pooled bytes, returned by the consumer.</summary>
    private readonly record struct ThreadSlice(byte[] Bytes, int Length);

    private sealed class ProfileMeta
    {
        public double DurationMs, IntervalMs;
        public string Product = "", Abi = "";
        public int? Cpus;

        public void Read(JsonDocument meta)
        {
            using (meta)
            {
                var root = meta.RootElement;
                DurationMs = root.TryGetProperty("profilingEndTime", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
                IntervalMs = root.TryGetProperty("interval", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetDouble() : 0;
                Product = root.TryGetProperty("product", out var p) ? p.GetString() ?? "" : "";
                Abi = root.TryGetProperty("abi", out var a) ? a.GetString() ?? "" : "";
                Cpus = root.TryGetProperty("logicalCPUs", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
            }
        }
    }

    /// <summary>
    /// Walks the top level of the profile: "meta" is parsed, each element of "threads" is cut out as a complete value and handed over,
    /// everything else is skipped.
    /// </summary>
    private sealed class ProfileStreamer(Stream input)
    {
        private byte[] _buffer = new byte[32 << 20];
        private int _start, _end;   // the unread bytes of the buffer
        private bool _eof;

        public void Run(ProfileMeta meta, Action<ThreadSlice> onThread)
        {
            var state = new JsonReaderState();
            Fill(1);
            Expect(ref state, JsonTokenType.StartObject);
            while (true)
            {
                var type = Peek(ref state);
                if (type is JsonTokenType.EndObject or JsonTokenType.None) break;
                var name = ReadPropertyName(ref state);
                if (name == "meta")
                {
                    var (start, length) = ReadValue(ref state);
                    meta.Read(JsonDocument.Parse(new ReadOnlyMemory<byte>(_buffer, start, length)));
                    _start = start + length;
                }
                else if (name == "threads")
                {
                    Expect(ref state, JsonTokenType.StartArray);
                    while (true)
                    {
                        if (Peek(ref state) == JsonTokenType.EndArray)
                        {
                            Expect(ref state, JsonTokenType.EndArray);
                            break;
                        }
                        var (start, length) = ReadValue(ref state);
                        var bytes = ArrayPool<byte>.Shared.Rent(length);
                        Buffer.BlockCopy(_buffer, start, bytes, 0, length);
                        _start = start + length;
                        onThread(new ThreadSlice(bytes, length));
                    }
                }
                else
                {
                    var (start, length) = ReadValue(ref state);
                    _start = start + length;
                }
            }
        }

        /// <summary>Compacts and reads until at least <paramref name="atLeast"/> unread bytes are in the buffer (or the end), growing it as needed.</summary>
        private void Fill(int atLeast)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            while (_end - _start < atLeast && !_eof)
            {
                if (_end == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
                int read = input.Read(_buffer, _end, _buffer.Length - _end);
                if (read == 0)
                {
                    _eof = true;
                    break;
                }
                _end += read;
            }
        }

        private Utf8JsonReader Reader(in JsonReaderState state) => new(_buffer.AsSpan(_start, _end - _start), _eof, state);

        /// <summary>The type of the next token, not consumed.</summary>
        private JsonTokenType Peek(ref JsonReaderState state)
        {
            while (true)
            {
                var reader = Reader(state);
                if (reader.Read()) return reader.TokenType;
                if (_eof) return JsonTokenType.None;
                Fill(_end - _start + 1);
            }
        }

        private void Expect(ref JsonReaderState state, JsonTokenType expected)
        {
            while (true)
            {
                var reader = Reader(state);
                if (reader.Read())
                {
                    if (reader.TokenType != expected) throw new InvalidDataException($"Expected {expected}, got {reader.TokenType}");
                    _start += (int)reader.BytesConsumed;
                    state = reader.CurrentState;
                    return;
                }
                if (_eof) throw new InvalidDataException("Unexpected end of the profile");
                Fill(_end - _start + 1);
            }
        }

        private string ReadPropertyName(ref JsonReaderState state)
        {
            while (true)
            {
                var reader = Reader(state);
                if (reader.Read())
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) throw new InvalidDataException($"Expected a property name, got {reader.TokenType}");
                    var name = reader.GetString()!;
                    _start += (int)reader.BytesConsumed;
                    state = reader.CurrentState;
                    return name;
                }
                if (_eof) throw new InvalidDataException("Unexpected end of the profile");
                Fill(_end - _start + 1);
            }
        }

        /// <summary>
        /// The next value (object, array or scalar) whole in the buffer: its start and length (the caller advances _start past it).
        /// When it does not fit, the unread bytes are doubled before trying again, so the retries are logarithmic in the value's size.
        /// </summary>
        private (int Start, int Length) ReadValue(ref JsonReaderState state)
        {
            while (true)
            {
                var reader = Reader(state);
                if (reader.Read() && (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray) || reader.TrySkip()))
                {
                    var consumed = (int)reader.BytesConsumed;
                    state = reader.CurrentState;
                    // the consumed bytes start at the previous token's end: skip the comma/colon/whitespace before the value
                    int start = _start;
                    while (start < _start + consumed && _buffer[start] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)',' or (byte)':') start++;
                    return (start, _start + consumed - start);
                }
                if (_eof) throw new InvalidDataException("Unexpected end of the profile");
                Fill(Math.Max(1 << 20, (_end - _start) * 2));
            }
        }
    }

    /// <summary>
    /// Parses one thread object with Utf8JsonReader into pooled arrays (any property order) and tallies it.
    /// </summary>
    private static partial class ThreadParser
    {
        public static void Parse(ReadOnlySpan<byte> json, Tally tally)
        {
            string name = "";
            string[]? strings = null;
            int[]? frameFunc = null, frameAddress = null, frameCategory = null, frameTier = null, stackFrame = null, stackPrefix = null, funcName = null, sampleStack = null;
            int frameTierCount = 0;
            long[]? weight = null, cpuDelta = null;
            double[]? sampleTime = null, sampleTimeDelta = null, times = null;
            int frameCount = 0, stackCount = 0, stackPrefixCount = 0, funcCount = 0, sampleCount = 0, weightCount = 0, cpuDeltaCount = 0, sampleTimeCount = 0, sampleTimeDeltaCount = 0;
            GcMarkers? markers = null;

            var reader = new Utf8JsonReader(json, true, default);
            reader.Read(); // {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("name"u8))
                {
                    reader.Read();
                    name = reader.GetString() ?? "";
                }
                else if (reader.ValueTextEquals("stringArray"u8)) strings = ReadStrings(ref reader);
                else if (reader.ValueTextEquals("frameTable"u8))
                {
                    var columns = new List<(byte[] Name, Action<int[], int> Set)>
                    {
                        ("func"u8.ToArray(), (a, c) => (frameFunc, frameCount) = (a, c)),
                        ("address"u8.ToArray(), (a, c) => (frameAddress, frameCount) = (a, c)),
                    };
                    // The categories of the frames (.NET or native) only for the threads with lock waits: the waiting code is the
                    // first .NET frame of the wait's stack outside the lock code
                    if (markers?.HasLockContention == true) columns.Add(("category"u8.ToArray(), (a, c) => frameCategory = a));
                    // The code version of each frame (its optimization tier: Tier0, Tier1, OSR...) for the code offsets
                    if (tally.CodeOffsets) columns.Add(("implementation"u8.ToArray(), (a, c) => (frameTier, frameTierCount) = (a, c)));
                    ReadTable(ref reader, columns.ToArray());
                }
                else if (reader.ValueTextEquals("stackTable"u8))
                {
                    // The prefixes (the paths to the root) are only used for the call trees, the heap bytes track and the lock
                    // waits: the name comes first in the files ultra writes, read them anyway when it is not known yet
                    if (tally.CallPaths || name.Length == 0 || name.EndsWith($"[{UltraHeapSnapshotConverter.BytesCounter}]", StringComparison.Ordinal) || markers?.HasLockContention == true)
                    {
                        ReadTable(ref reader, ("frame"u8.ToArray(), (a, c) => (stackFrame, stackCount) = (a, c)), ("prefix"u8.ToArray(), (a, c) => (stackPrefix, stackPrefixCount) = (a, c)));
                    }
                    else
                    {
                        ReadTable(ref reader, ("frame"u8.ToArray(), (a, c) => (stackFrame, stackCount) = (a, c)));
                    }
                }
                else if (reader.ValueTextEquals("funcTable"u8)) ReadTable(ref reader, ("name"u8.ToArray(), (a, c) => (funcName, funcCount) = (a, c)));
                else if (reader.ValueTextEquals("samples"u8))
                {
                    reader.Read(); // {
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("stack"u8)) (sampleStack, sampleCount) = ReadInts(ref reader);
                        else if (reader.ValueTextEquals("weight"u8)) (weight, weightCount) = ReadLongs(ref reader);
                        else if (reader.ValueTextEquals("threadCPUDelta"u8)) (cpuDelta, cpuDeltaCount) = ReadLongs(ref reader);
                        else if (tally.Windowed && reader.ValueTextEquals("time"u8)) (sampleTime, sampleTimeCount) = ReadDoubles(ref reader);
                        else if (tally.Windowed && reader.ValueTextEquals("timeDeltas"u8)) (sampleTimeDelta, sampleTimeDeltaCount) = ReadDoubles(ref reader);
                        else Skip(ref reader);
                    }
                }
                else if (reader.ValueTextEquals("markers"u8)) markers = ReadMarkers(ref reader);
                else Skip(ref reader);
            }

            try
            {
                if (markers != null)
                {
                    // The code a lock wait was in: from its stack, the first frame outside the lock code
                    var stringTable = strings ?? [];
                    Func<int, string> waitingIn = stack => WaitingIn(stack, stringTable, stackFrame, stackCount, stackPrefix, stackPrefixCount, frameFunc, frameCategory, funcName);
                    tally.AddMarkers(name, stringTable, markers, waitingIn);
                }
                if (tally.Windowed) times = SampleTimes(sampleCount, sampleTime, sampleTimeCount, sampleTimeDelta, sampleTimeDeltaCount);
                tally.Add(name, strings ?? [], frameFunc, frameAddress, frameCount, stackFrame, stackCount, stackPrefix, stackPrefixCount, funcName, funcCount, sampleStack, sampleCount, weight, weightCount, cpuDelta, cpuDeltaCount, times, frameTier, frameTierCount);
            }
            finally
            {
                Return(frameFunc); Return(frameAddress); Return(frameCategory); Return(frameTier); Return(stackFrame); Return(stackPrefix); Return(funcName); Return(sampleStack); Return(weight); Return(cpuDelta);
                Return(sampleTime); Return(sampleTimeDelta); Return(times);
                if (markers != null) { Return(markers.Name); Return(markers.Start); Return(markers.End); }
            }
        }

        /// <summary>
        /// The time of each sample in ms from the start of the profile (pooled): "time" when the file has it, else the running sum of
        /// "timeDeltas" (what ultra writes; the scale of the markers and of the MCP tools' windows).
        /// </summary>
        private static double[] SampleTimes(int count, double[]? time, int timeCount, double[]? deltas, int deltaCount)
        {
            var times = ArrayPool<double>.Shared.Rent(Math.Max(1, count));
            if (time != null && timeCount >= count)
            {
                Array.Copy(time, times, count);
                return times;
            }
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                if (deltas != null && i < deltaCount && !double.IsNaN(deltas[i])) sum += deltas[i];
                times[i] = sum;
            }
            return times;
        }

        private static void Skip(ref Utf8JsonReader reader)
        {
            reader.Read();
            reader.TrySkip();
        }

        private static void Return<T>(T[]? array)
        {
            if (array != null) ArrayPool<T>.Shared.Return(array);
        }

        private static void ReadTable(ref Utf8JsonReader reader, params (byte[] Name, Action<int[], int> Set)[] columns)
        {
            reader.Read(); // {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool taken = false;
                foreach (var (column, set) in columns)
                {
                    if (reader.ValueTextEquals(column))
                    {
                        var (array, count) = ReadInts(ref reader);
                        set(array, count);
                        taken = true;
                        break;
                    }
                }
                if (!taken) Skip(ref reader);
            }
        }

        private static string[] ReadStrings(ref Utf8JsonReader reader)
        {
            reader.Read(); // [
            var list = new List<string>(1024);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) list.Add(reader.GetString() ?? "");
            return list.ToArray();
        }

        /// <summary>An array of numbers into a pooled int array; null or too large becomes -1.</summary>
        private static (int[] Array, int Count) ReadInts(ref Utf8JsonReader reader)
        {
            reader.Read(); // [ (or null)
            if (reader.TokenType != JsonTokenType.StartArray) return (ArrayPool<int>.Shared.Rent(0), 0);
            var array = ArrayPool<int>.Shared.Rent(4096);
            int count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (count == array.Length) Grow(ref array);
                array[count++] = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v) ? v : -1;
            }
            return (array, count);
        }

        private static (long[] Array, int Count) ReadLongs(ref Utf8JsonReader reader)
        {
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray) return (ArrayPool<long>.Shared.Rent(0), 0);
            var array = ArrayPool<long>.Shared.Rent(4096);
            int count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (count == array.Length) Grow(ref array);
                array[count++] = reader.TokenType == JsonTokenType.Number ? (reader.TryGetInt64(out var v) ? v : (long)reader.GetDouble()) : 0;
            }
            return (array, count);
        }

        private static void Grow<T>(ref T[] array)
        {
            var bigger = ArrayPool<T>.Shared.Rent(array.Length * 2);
            array.AsSpan().CopyTo(bigger);
            ArrayPool<T>.Shared.Return(array);
            array = bigger;
        }
    }

    /// <summary>The tallies of one worker: per thread rows, leaf function counts, code offsets, counter events, and the call paths of the trees.</summary>
    private sealed partial class Tally(UltraProfileReport report, NameTable? names)
    {
        [GeneratedRegex(@"\[([^\]]+)\]$")]
        private static partial Regex CounterSuffix();

        private readonly bool offsets = report.CodeOffsets;
        private readonly string? threadFilter = report.ThreadFilter;
        private readonly TimeWindow window = report.Window ?? default;

        public readonly List<(string Name, int Samples, double CpuMs)> CpuThreads = new();
        public readonly Dictionary<string, long> CpuLeaf = new();

        /// <summary>The samples per leaf function, code version (its optimization tier, "" when the trace has none) and code offset.</summary>
        public readonly Dictionary<string, Dictionary<(string Tier, int Offset), long>> CpuOffsets = new();

        /// <summary>The samples per leaf function and code version (with or without an address): where the function ran from.</summary>
        public readonly Dictionary<string, Dictionary<string, long>> CpuTiers = new();

        /// <summary>Whether the code offsets are tallied: the parser then reads the code version of the frames.</summary>
        public bool CodeOffsets => offsets;
        public readonly Dictionary<string, Dictionary<string, long>> CounterLeaf = new();
        public readonly Dictionary<string, (int Tracks, long Samples, long Events)> Counters = new();
        public readonly GcTally Gc = new();
        public readonly HeapTally Heap = new();
        public readonly LockTally Locks = new();
        public int Threads;

        /// <summary>The names of the functions of the profile (compact text: a name loses its parameters only when no other has the same).</summary>
        public readonly HashSet<string> FunctionNames = new(StringComparer.Ordinal);

        /// <summary>Whether only the samples and markers of a time window are counted: the parser then reads the sample times.</summary>
        public bool Windowed => report.Window.HasValue;

        /// <summary>Whether the call paths of the CPU threads are kept for the trees: the parser then reads the stack prefixes.</summary>
        public bool CallPaths => names != null;

        private bool InWindow(double[]? times, int sample) => times == null || window.Contains(times[sample]);

        /// <summary>The GC markers of a thread (none on the counter tracks); its allocations only when it passes the thread filter.</summary>
        public void AddMarkers(string name, string[] strings, GcMarkers markers, Func<int, string> waitingIn)
        {
            if (CounterSuffix().Match(name) is { Success: true } counter)
            {
                // The heap bytes track carries the summary of its snapshot (what was lost while it was taken)
                if (counter.Groups[1].Value == UltraHeapSnapshotConverter.BytesCounter)
                {
                    foreach (var marker in markers.Data)
                    {
                        if (marker is { Kind: GcMarkerKind.HeapSnapshot, Counts: { } counts }) Heap.AddSummary(counts);
                    }
                }
                return;
            }
            var passesFilter = threadFilter == null || name.Contains(threadFilter, StringComparison.OrdinalIgnoreCase);
            TimeWindow? markerWindow = Windowed ? window : null;
            Gc.Add(name, strings, markers, passesFilter, markerWindow);
            if (passesFilter && markers.HasLockContention) Locks.Add(name, markers, waitingIn, markerWindow);
        }

        public void Add(string name, string[] strings, int[]? frameFunc, int[]? frameAddress, int frameCount, int[]? stackFrame, int stackCount, int[]? stackPrefix, int stackPrefixCount, int[]? funcName, int funcCount,
            int[]? sampleStack, int sampleCount, long[]? weight, int weightCount, long[]? cpuDelta, int cpuDeltaCount, double[]? times, int[]? frameTier, int frameTierCount)
        {
            Threads++;
            Locks.AddThreadName(name);
            var counter = CounterSuffix().Match(name) is { Success: true } m ? m.Groups[1].Value : null;

            // The GC heap snapshot tracks: for the whole process, so not filtered (nor windowed: the snapshot is taken at the end)
            if (counter is UltraHeapSnapshotConverter.BytesCounter or UltraHeapSnapshotConverter.ObjectsCounter)
            {
                if (sampleStack != null && stackFrame != null && frameFunc != null && funcName != null)
                {
                    Heap.Add(counter == UltraHeapSnapshotConverter.BytesCounter, strings, frameFunc, stackFrame, stackPrefix, stackPrefixCount, funcName, sampleStack, sampleCount, weight, weightCount);
                }
                return;
            }

            if (report.Compact && funcName != null)
            {
                for (int f = 0; f < funcCount; f++)
                {
                    if ((uint)funcName[f] < (uint)strings.Length) FunctionNames.Add(strings[funcName[f]]);
                }
            }

            // a thread outside the filter is not in the report: no row and no count of its own, as a CPU thread or a counter track
            if (threadFilter != null && !name.Contains(threadFilter, StringComparison.OrdinalIgnoreCase)) return;
            if (sampleCount == 0 || sampleStack == null || stackFrame == null || frameFunc == null || funcName == null)
            {
                if (counter == null) CpuThreads.Add((name, 0, 0));
                return;
            }

            if (counter != null)
            {
                var agg = CounterLeaf.TryGetValue(counter, out var existing) ? existing : CounterLeaf[counter] = new();
                long total = 0;
                int inWindow = 0;
                for (int i = 0; i < sampleCount; i++)
                {
                    if (!InWindow(times, i)) continue;
                    var w = weight != null && i < weightCount ? weight[i] : 1;
                    var leaf = strings[funcName[frameFunc[stackFrame[sampleStack[i]]]]];
                    agg[leaf] = agg.GetValueOrDefault(leaf) + w;
                    total += w;
                    inWindow++;
                }
                var c = Counters.GetValueOrDefault(counter);
                Counters[counter] = (c.Tracks + 1, c.Samples + inWindow, c.Events + total);
                return;
            }

            double cpuMs = 0;
            int samples = sampleCount;
            if (times == null)
            {
                if (cpuDelta != null)
                {
                    for (int i = 0; i < cpuDeltaCount; i++) cpuMs += cpuDelta[i];
                }
            }
            else
            {
                samples = 0;
                for (int i = 0; i < sampleCount; i++)
                {
                    if (!InWindow(times, i)) continue;
                    samples++;
                    if (cpuDelta != null && i < cpuDeltaCount) cpuMs += cpuDelta[i];
                }
            }
            cpuMs /= 1e6;
            CpuThreads.Add((name, samples, cpuMs));
            if (names != null && samples > 0)
            {
                AddCallPaths(name, cpuMs, samples, strings, frameFunc, frameCount, stackFrame, stackCount, stackPrefix, stackPrefixCount, funcName, funcCount, sampleStack, sampleCount, times);
            }
            for (int i = 0; i < sampleCount; i++)
            {
                if (!InWindow(times, i)) continue;
                var frame = stackFrame[sampleStack[i]];
                var leaf = strings[funcName[frameFunc[frame]]];
                CpuLeaf[leaf] = CpuLeaf.GetValueOrDefault(leaf) + 1;
                if (!offsets) continue;

                // The code version of the frame: its tier (Tier0, Tier1, OSR...), "" when the trace has none (native code, older
                // conversions, Linux); the offsets of two versions of a function are offsets in different code
                var tierIndex = frameTier != null && frame < frameTierCount ? frameTier[frame] : -1;
                var tier = (uint)tierIndex < (uint)strings.Length ? strings[tierIndex] : "";
                var tiers = CpuTiers.TryGetValue(leaf, out var t) ? t : CpuTiers[leaf] = new(StringComparer.Ordinal);
                tiers[tier] = tiers.GetValueOrDefault(tier) + 1;

                // -1 is no address; the others are int32 offsets, which wrap negative far from a module's base (JIT'd code in traces
                // converted before the offsets became offsets in the code version: all the offsets of a run were lost when skipped)
                if (frameAddress != null && frame < frameCount && frameAddress[frame] != -1)
                {
                    var byOffset = CpuOffsets.TryGetValue(leaf, out var o) ? o : CpuOffsets[leaf] = new();
                    var key = (tier, frameAddress[frame]);
                    byOffset[key] = byOffset.GetValueOrDefault(key) + 1;
                }
            }
        }
    }
}
