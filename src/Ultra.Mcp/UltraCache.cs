// Copyright (c) 2026 Kirill Osenkov. Licensed under the MIT license.
// Imported from https://github.com/KirillOsenkov/UltraMcp (commit 434b5bc); see THIRD-PARTY-NOTICES.txt.

using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Ultra.Mcp;

internal sealed class LoadedProfile
{
    public required string Path { get; init; }

    public required FirefoxProfiler.Profile Profile { get; init; }

    public long FileSize { get; init; }

    public DateTime LastWriteTimeUtc { get; init; }

    public DateTime LoadedAtUtc { get; init; }

    public DateTime LastAccessedUtc { get; set; }
}

/// <summary>
/// Caches loaded Ultra / Firefox-Profiler .json profiles keyed by full path.
/// LRU eviction against a coarse memory budget. Mirrors the BeetleCache /
/// BinlogCache design.
/// </summary>
internal sealed class UltraCache
{
    // The deserialized object graph (parallel List<int> tables) is roughly this
    // multiple of the JSON file size in memory.
    public const long MemoryMultiplier = 15;

    private readonly object syncRoot = new();
    // Serializes the slow load path so two callers can't race deserializing.
    private readonly object loadLock = new();
    private readonly Dictionary<string, LoadedProfile> entries = new(PathComparer);

    public UltraCache(long? memoryBudgetBytes = null)
    {
        MemoryBudgetBytes = memoryBudgetBytes ?? GetDefaultMemoryBudget();
    }

    public long MemoryBudgetBytes { get; }

    public LoadedProfile Load(string path, bool forceReload = false)
    {
        path = NormalizePath(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Ultra profile not found: {path}", path);
        }

        var info = new FileInfo(path);

        if (!forceReload && TryGetFresh(path, info, out var cached))
        {
            return cached!;
        }

        lock (loadLock)
        {
            if (!forceReload && TryGetFresh(path, info, out cached))
            {
                return cached!;
            }

            // Load before evicting so a failure doesn't drop the previously-cached version.
            FirefoxProfiler.Profile profile;
            using (var fileStream = File.OpenRead(path))
            {
                // Ultra also emits gzip-compressed profiles (.json.gz). Transparently
                // decompress those; a plain .json opens the file stream directly.
                Stream stream = fileStream;
                if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    stream = new GZipStream(fileStream, CompressionMode.Decompress);
                }

                try
                {
                    profile = JsonSerializer.Deserialize(stream, FirefoxProfiler.JsonProfilerContext.Default.Profile)
                        ?? throw new InvalidDataException($"Deserialized a null profile from {path}.");
                }
                finally
                {
                    if (stream != fileStream)
                    {
                        stream.Dispose();
                    }
                }
            }

            var entry = new LoadedProfile
            {
                Path = path,
                Profile = profile,
                FileSize = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                LoadedAtUtc = DateTime.UtcNow,
                LastAccessedUtc = DateTime.UtcNow
            };

            long estimated = info.Length * MemoryMultiplier;
            lock (syncRoot)
            {
                if (entries.Remove(path))
                {
                    ForceCollect();
                }

                EvictToFit(estimated);
                entries[path] = entry;
            }

            return entry;
        }
    }

    private bool TryGetFresh(string path, FileInfo info, out LoadedProfile? entry)
    {
        lock (syncRoot)
        {
            if (entries.TryGetValue(path, out var cached) &&
                cached.FileSize == info.Length &&
                cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                cached.LastAccessedUtc = DateTime.UtcNow;
                entry = cached;
                return true;
            }
        }

        entry = null;
        return false;
    }

    public bool Unload(string path)
    {
        path = NormalizePath(path);
        lock (syncRoot)
        {
            if (entries.Remove(path))
            {
                ForceCollect();
                return true;
            }

            return false;
        }
    }

    public int UnloadAll()
    {
        lock (syncRoot)
        {
            int n = entries.Count;
            entries.Clear();
            if (n > 0)
            {
                ForceCollect();
            }

            return n;
        }
    }

    public IReadOnlyList<LoadedProfile> List()
    {
        lock (syncRoot)
        {
            return entries.Values.ToArray();
        }
    }

    /// <summary>
    /// Returns the most-recently-accessed cached entry, or null if the cache is
    /// empty. Used by tools that accept an implicit path argument.
    /// </summary>
    public LoadedProfile? TryGetMostRecent()
    {
        lock (syncRoot)
        {
            LoadedProfile? best = null;
            foreach (var e in entries.Values)
            {
                if (best == null || e.LastAccessedUtc > best.LastAccessedUtc)
                {
                    best = e;
                }
            }

            return best;
        }
    }

    // Caller holds syncRoot.
    private void EvictToFit(long incoming)
    {
        if (incoming >= MemoryBudgetBytes)
        {
            if (entries.Count > 0)
            {
                entries.Clear();
                ForceCollect();
            }

            return;
        }

        long used = entries.Values.Sum(e => e.FileSize * MemoryMultiplier);
        if (used + incoming <= MemoryBudgetBytes)
        {
            return;
        }

        var lru = entries.Values.OrderBy(e => e.LastAccessedUtc).ToList();
        foreach (var e in lru)
        {
            entries.Remove(e.Path);
            used -= e.FileSize * MemoryMultiplier;
            if (used + incoming <= MemoryBudgetBytes)
            {
                break;
            }
        }

        ForceCollect();
    }

    private static void ForceCollect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static long GetDefaultMemoryBudget()
    {
        var info = GC.GetGCMemoryInfo();
        return (long)(info.TotalAvailableMemoryBytes * 0.75);
    }
}
