// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Ultra.Core.Report;

/// <summary>
/// A text report of a Firefox profile written by ultra, without loading it whole: the CPU time per thread and its share, the top
/// leaf functions over all threads, the hot code offsets of the top functions, and the events of each hardware counter track per
/// function. The <c>[counter]</c> tracks of <c>--pmc</c> are reported apart from the CPU threads (a sum over all threads would count
/// them as CPU).
/// </summary>
/// <remarks>
/// The profile is streamed thread by thread: one thread inflates the gzip and cuts the JSON into complete thread objects, workers
/// parse them with <see cref="Utf8JsonReader"/> into pooled arrays (no DOM) and tally them, the tallies are merged at the end. A
/// 150 MB trace of a 2 minute run on 12 cores takes a few seconds, bound by the inflate.
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
    {
        Top = Math.Max(1, top);
        CodeOffsets = codeOffsets;
        ThreadFilter = threadFilter;
        Workers = workers > 0 ? workers : Math.Max(1, Environment.ProcessorCount / 2);
    }

    /// <summary>How many threads and functions to list.</summary>
    public int Top { get; }

    /// <summary>Whether the hot code offsets of the top functions are listed.</summary>
    public bool CodeOffsets { get; }

    /// <summary>Only the threads whose name contains this, when not null.</summary>
    public string? ThreadFilter { get; }

    /// <summary>The parsing threads.</summary>
    public int Workers { get; }

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
        for (int w = 0; w < Workers; w++)
        {
            var tally = tallies[w] = new Tally(CodeOffsets, ThreadFilter);
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
        var threads = tallies.Sum(t => t.Threads);
        var totalCpu = cpuThreads.Sum(x => x.CpuMs);

        o.WriteLine($"product={meta.Product} abi={meta.Abi} cpus={meta.Cpus} duration={meta.DurationMs / 1000:F1} s");
        o.WriteLine();
        o.WriteLine($"{threads} threads in the file: {cpuThreads.Count} CPU threads and {counters.Values.Sum(x => x.Tracks)} counter tracks{(counters.Count > 0 ? $" ({string.Join(", ", counters.OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Value.Tracks}"))})" : "")}");
        o.WriteLine($"CPU: {totalCpu / 1000:F1} s over {meta.DurationMs / 1000:F1} s = {totalCpu / Math.Max(1, meta.DurationMs):F2} cores busy on average, {cpuThreads.Sum(x => (long)x.Samples):N0} samples");
        o.WriteLine("per thread (samples, CPU ms, share of the process CPU):");
        var rows = cpuThreads.OrderByDescending(x => x.CpuMs).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        foreach (var (name, n, cpu) in rows.Take(Top))
        {
            o.WriteLine($"  {name,-44} {n,9:N0} {cpu,10:N0} ms {(totalCpu > 0 ? cpu / totalCpu * 100 : 0),5:F1}%");
        }
        if (rows.Count > Top)
        {
            var rest = rows.Skip(Top).Sum(x => x.CpuMs);
            o.WriteLine($"  ... {rows.Count - Top} more threads {rest,24:N0} ms {(totalCpu > 0 ? rest / totalCpu * 100 : 0),5:F1}%");
        }

        var n2 = cpuLeaf.Values.Sum();
        o.WriteLine();
        o.WriteLine($"top leaf functions over all CPU threads ({n2:N0} samples):");
        foreach (var (leaf, c) in cpuLeaf.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(Top))
        {
            o.WriteLine($"  {c,10:N0} {c * 100.0 / Math.Max(1, n2),5:F1}%  {leaf}");
        }

        if (CodeOffsets)
        {
            foreach (var (leaf, _) in cpuLeaf.OrderByDescending(x => x.Value).Take(3))
            {
                var byOffset = Merge(tallies.Select(t => t.CpuOffsets.GetValueOrDefault(leaf)).Where(x => x != null)!);
                if (byOffset.Count == 0) continue;
                var total = byOffset.Values.Sum();
                o.WriteLine();
                o.WriteLine($"hot code offsets in {leaf} ({total:N0} samples with an address, {byOffset.Count} distinct):");
                foreach (var (offset, c) in byOffset.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(10))
                {
                    o.WriteLine($"  +0x{offset:x}: {c:N0} ({c * 100.0 / total:F1}%)");
                }
            }
        }

        foreach (var (counter, (tracks, samples, events)) in counters.OrderBy(x => x.Key))
        {
            var leafs = Merge(tallies.Select(t => t.CounterLeaf.GetValueOrDefault(counter)).Where(x => x != null)!);
            o.WriteLine();
            o.WriteLine($"counter [{counter}]: {tracks} tracks, {samples:N0} samples standing for {events:N0} events; top leaf functions by events:");
            foreach (var (leaf, c) in leafs.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(Top))
            {
                o.WriteLine($"  {c,16:N0} {(events > 0 ? c * 100.0 / events : 0),5:F1}%  {leaf}");
            }
        }
    }

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
        public double DurationMs;
        public string Product = "", Abi = "";
        public int? Cpus;

        public void Read(JsonDocument meta)
        {
            using (meta)
            {
                var root = meta.RootElement;
                DurationMs = root.TryGetProperty("profilingEndTime", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
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
    private static class ThreadParser
    {
        public static void Parse(ReadOnlySpan<byte> json, Tally tally)
        {
            string name = "";
            string[]? strings = null;
            int[]? frameFunc = null, frameAddress = null, stackFrame = null, funcName = null, sampleStack = null;
            long[]? weight = null, cpuDelta = null;
            int frameCount = 0, stackCount = 0, funcCount = 0, sampleCount = 0, weightCount = 0, cpuDeltaCount = 0;

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
                else if (reader.ValueTextEquals("frameTable"u8)) ReadTable(ref reader, ("func"u8.ToArray(), (a, c) => (frameFunc, frameCount) = (a, c)), ("address"u8.ToArray(), (a, c) => (frameAddress, frameCount) = (a, c)));
                else if (reader.ValueTextEquals("stackTable"u8)) ReadTable(ref reader, ("frame"u8.ToArray(), (a, c) => (stackFrame, stackCount) = (a, c)));
                else if (reader.ValueTextEquals("funcTable"u8)) ReadTable(ref reader, ("name"u8.ToArray(), (a, c) => (funcName, funcCount) = (a, c)));
                else if (reader.ValueTextEquals("samples"u8))
                {
                    reader.Read(); // {
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("stack"u8)) (sampleStack, sampleCount) = ReadInts(ref reader);
                        else if (reader.ValueTextEquals("weight"u8)) (weight, weightCount) = ReadLongs(ref reader);
                        else if (reader.ValueTextEquals("threadCPUDelta"u8)) (cpuDelta, cpuDeltaCount) = ReadLongs(ref reader);
                        else Skip(ref reader);
                    }
                }
                else Skip(ref reader);
            }

            try
            {
                tally.Add(name, strings ?? [], frameFunc, frameAddress, frameCount, stackFrame, stackCount, funcName, funcCount, sampleStack, sampleCount, weight, weightCount, cpuDelta, cpuDeltaCount);
            }
            finally
            {
                Return(frameFunc); Return(frameAddress); Return(stackFrame); Return(funcName); Return(sampleStack); Return(weight); Return(cpuDelta);
            }
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

    /// <summary>The tallies of one worker: per thread rows, leaf function counts, code offsets, counter events.</summary>
    private sealed partial class Tally(bool offsets, string? threadFilter)
    {
        [GeneratedRegex(@"\[([^\]]+)\]$")]
        private static partial Regex CounterSuffix();

        public readonly List<(string Name, int Samples, double CpuMs)> CpuThreads = new();
        public readonly Dictionary<string, long> CpuLeaf = new();
        public readonly Dictionary<string, Dictionary<int, long>> CpuOffsets = new();
        public readonly Dictionary<string, Dictionary<string, long>> CounterLeaf = new();
        public readonly Dictionary<string, (int Tracks, long Samples, long Events)> Counters = new();
        public int Threads;

        public void Add(string name, string[] strings, int[]? frameFunc, int[]? frameAddress, int frameCount, int[]? stackFrame, int stackCount, int[]? funcName, int funcCount,
            int[]? sampleStack, int sampleCount, long[]? weight, int weightCount, long[]? cpuDelta, int cpuDeltaCount)
        {
            Threads++;
            var counter = CounterSuffix().Match(name) is { Success: true } m ? m.Groups[1].Value : null;
            if (threadFilter != null && !name.Contains(threadFilter, StringComparison.OrdinalIgnoreCase)) sampleCount = 0;
            if (sampleCount == 0 || sampleStack == null || stackFrame == null || frameFunc == null || funcName == null)
            {
                if (counter == null) CpuThreads.Add((name, 0, 0));
                return;
            }

            if (counter != null)
            {
                var agg = CounterLeaf.TryGetValue(counter, out var existing) ? existing : CounterLeaf[counter] = new();
                long total = 0;
                for (int i = 0; i < sampleCount; i++)
                {
                    var w = weight != null && i < weightCount ? weight[i] : 1;
                    var leaf = strings[funcName[frameFunc[stackFrame[sampleStack[i]]]]];
                    agg[leaf] = agg.GetValueOrDefault(leaf) + w;
                    total += w;
                }
                var c = Counters.GetValueOrDefault(counter);
                Counters[counter] = (c.Tracks + 1, c.Samples + sampleCount, c.Events + total);
                return;
            }

            double cpuMs = 0;
            if (cpuDelta != null)
            {
                for (int i = 0; i < cpuDeltaCount; i++) cpuMs += cpuDelta[i];
            }
            cpuMs /= 1e6;
            CpuThreads.Add((name, sampleCount, cpuMs));
            for (int i = 0; i < sampleCount; i++)
            {
                var frame = stackFrame[sampleStack[i]];
                var leaf = strings[funcName[frameFunc[frame]]];
                CpuLeaf[leaf] = CpuLeaf.GetValueOrDefault(leaf) + 1;
                if (offsets && frameAddress != null && frame < frameCount && frameAddress[frame] >= 0)
                {
                    var byOffset = CpuOffsets.TryGetValue(leaf, out var o) ? o : CpuOffsets[leaf] = new();
                    byOffset[frameAddress[frame]] = byOffset.GetValueOrDefault(frameAddress[frame]) + 1;
                }
            }
        }
    }
}
