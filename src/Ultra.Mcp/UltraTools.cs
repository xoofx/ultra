// Copyright (c) 2026 Kirill Osenkov. Licensed under the MIT license.
// Imported from https://github.com/KirillOsenkov/UltraMcp (commit 434b5bc); see THIRD-PARTY-NOTICES.txt.

using ModelContextProtocol;

namespace Ultra.Mcp;

internal static partial class UltraTools
{
    internal static readonly UltraCache Cache = new();

    public const int DefaultMaxResults = 200;

    public const int MaxAllowedResults = 5000;

    /// <summary>
    /// Resolves the profile for a tool call. If <paramref name="path"/> is provided,
    /// loads / returns the cached entry for that path. If null/empty, returns the
    /// most-recently-accessed cached entry, or throws a clear McpException when the
    /// cache is empty.
    /// </summary>
    internal static LoadedProfile ResolveProfile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return Cache.Load(path);
        }

        var recent = Cache.TryGetMostRecent();
        if (recent != null)
        {
            return recent;
        }

        throw new McpException(
            "No 'path' argument was supplied and no profile is currently loaded. " +
            "Call load_profile <path> first, or pass an explicit absolute path to this tool.");
    }

    // The MCP SDK only forwards messages from McpException; other exceptions surface
    // as a generic "An error occurred invoking '<tool>'."
    internal static T Run<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Validates a thread index against the profile and returns the thread.
    /// </summary>
    internal static FirefoxProfiler.Thread ResolveThread(LoadedProfile entry, int threadIndex)
    {
        var threads = entry.Profile.Threads;
        if ((uint)threadIndex >= (uint)threads.Count)
        {
            throw new McpException(
                $"threadIndex {threadIndex} is out of range. This profile has {threads.Count} thread(s). " +
                "Call list_threads to see them.");
        }

        return threads[threadIndex];
    }

    /// <summary>
    /// Resolves the display name of a func row in a thread, with its owning module
    /// as "module!func" when a resource is present.
    /// </summary>
    internal static string FuncName(FirefoxProfiler.Thread thread, int funcIndex)
    {
        var funcTable = thread.FuncTable;
        string name = SafeString(thread, funcTable.Name[funcIndex]);

        int resource = funcTable.Resource[funcIndex];
        if (resource >= 0 && resource < thread.ResourceTable.Length)
        {
            int nameIdx = thread.ResourceTable.Name[resource];
            string module = SafeString(thread, nameIdx);
            if (!string.IsNullOrEmpty(module))
            {
                return module + "!" + name;
            }
        }

        return name;
    }

    internal static string SafeString(FirefoxProfiler.Thread thread, int index)
    {
        if ((uint)index >= (uint)thread.StringArray.Count)
        {
            return "<?>";
        }

        return thread.StringArray[index] ?? string.Empty;
    }
}
