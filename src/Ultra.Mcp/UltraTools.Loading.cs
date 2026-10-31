// Copyright (c) 2026 Kirill Osenkov. Licensed under the MIT license.
// Imported from https://github.com/KirillOsenkov/UltraMcp (commit 434b5bc); see THIRD-PARTY-NOTICES.txt.

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace Ultra.Mcp;

[McpServerToolType]
internal static partial class UltraTools
{
    [McpServerTool(Name = "load_profile", ReadOnly = true, Idempotent = true)]
    [Description(@"Loads an Ultra / Firefox-Profiler .json profile into memory and returns a summary. Optional: any tool that takes a profile path will load it implicitly if not already cached. Use this to warm the cache or to inspect the summary up front.")]
    public static string LoadProfile(
        [Description("Absolute path to a profile .json file")] string path)
        => Run(() => Describe(Cache.Load(path)));

    [McpServerTool(Name = "reload_profile", ReadOnly = true, Idempotent = true)]
    [Description(@"Re-reads a profile from disk, replacing the cached version. Use this if the file has been re-generated. threadIndex / row values are scoped to one file's bytes — discard previously returned ids after a reload that produced different content.")]
    public static string ReloadProfile(
        [Description("Absolute path to a profile .json file")] string path)
        => Run(() => Describe(Cache.Load(path, forceReload: true)));

    [McpServerTool(Name = "unload_profile", ReadOnly = true, Idempotent = true)]
    [Description("Evicts a single profile from the cache to free memory.")]
    public static string UnloadProfile(
        [Description("Absolute path to the profile .json file to evict")] string path)
        => Run(() => Cache.Unload(path) ? $"unloaded {path}" : $"not loaded: {path}");

    [McpServerTool(Name = "unload_all_profiles", ReadOnly = true, Idempotent = true)]
    [Description("Evicts all loaded profiles from the cache to free memory.")]
    public static string UnloadAllProfiles()
        => Run(() => $"unloaded {Cache.UnloadAll()} profile(s)");

    [McpServerTool(Name = "list_loaded_profiles", ReadOnly = true, Idempotent = true)]
    [Description("Lists all profiles currently loaded in the cache, with file size and last-access timestamp.")]
    public static string ListLoadedProfiles() => Run(() =>
    {
        var entries = Cache.List();
        if (entries.Count == 0)
        {
            return "no profiles loaded";
        }

        return string.Join("\n", entries
            .OrderByDescending(e => e.LastAccessedUtc)
            .Select(e => $"{e.Path}\tfileSize={e.FileSize:n0}\tlastAccessed={e.LastAccessedUtc:o}"));
    });

    [McpServerTool(Name = "get_profile_summary", ReadOnly = true, Idempotent = true)]
    [Description(@"Returns a one-page overview of a loaded profile: file size, product, interval, recording window, CPU count, and counts of libs, threads, and total samples. Always cheap — call this first on any unfamiliar file.")]
    public static string GetProfileSummary(
        [Description("Absolute path to a profile .json file. Optional: defaults to the most recently loaded profile.")] string? path = null)
        => Run(() => Describe(ResolveProfile(path)));

    private static string Describe(LoadedProfile entry)
    {
        var profile = entry.Profile;
        var meta = profile.Meta;

        long totalSamples = 0;
        foreach (var t in profile.Threads)
        {
            totalSamples += t.Samples.Length;
        }

        var sb = new StringBuilder();
        sb.Append("path: ").AppendLine(entry.Path);
        sb.Append("fileSize: ").Append(entry.FileSize.ToString("n0")).AppendLine(" bytes");
        sb.Append("product: ").AppendLine(meta.Product);
        sb.Append("intervalMs: ").AppendLine(meta.Interval.ToString("0.####"));
        sb.Append("profilingStartMs: ").AppendLine((meta.ProfilingStartTime ?? 0).ToString("0.###"));
        sb.Append("profilingEndMs: ").AppendLine((meta.ProfilingEndTime ?? 0).ToString("0.###"));
        sb.Append("logicalCPUs: ").AppendLine((meta.LogicalCPUs ?? 0).ToString());
        sb.Append("oscpu: ").AppendLine(meta.Oscpu ?? string.Empty);
        sb.Append("libs: ").AppendLine(profile.Libs.Count.ToString());
        sb.Append("threads: ").AppendLine(profile.Threads.Count.ToString());
        sb.Append("totalSamples: ").Append(totalSamples);
        return sb.ToString();
    }
}
