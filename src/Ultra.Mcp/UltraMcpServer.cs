// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Ultra.Core.Automation;

namespace Ultra.Mcp;

/// <summary>
/// The MCP server of ultra (`ultra mcp`): stdio transport, the profile analysis tools (from UltraMcp) and the capture tools.
/// </summary>
internal static class UltraMcpServer
{
    /// <summary>
    /// The instructions given to the MCP client at initialization.
    /// </summary>
    public const string ServerInstructions = """
        Ultra profiler for .NET (Windows ETW, Linux perf, macOS ARM64): capture CPU profiles and analyze them.

        Capture: find_processes -> start_profiling (pids, or a program to launch; on Windows it shows
        a UAC prompt to the user unless ultra already runs as administrator) -> get_job_status with
        waitSeconds until the job ends (or stop_job to stop the capture early and still write the profile).
        The job result lists the written .json.gz profile files. start_conversion converts an existing
        trace (.etl, .ultraperf, .nettrace) the same way.

        Analysis: each profile is a Firefox-Profiler-format .json(.gz) file with a columnar per-thread
        layout (samples, stackTable, frameTable, funcTable, resourceTable, stringArray, markers) cross-
        referenced by integer index. Standard flow: get_profile_report (one-shot text overview: CPU per
        thread, top functions) or load_profile -> get_profile_summary / list_threads. For slow pockets:
        call_tree (inclusive top-down tree), call_tree_inverted (bottom-up: callers of a function),
        find_hotspots (search by function / type name), focus_function (one-hop callers + callees),
        module_breakdown (time by layer). Localize a spike with the startMs/endMs window on any
        aggregation tool, or list_samples -> get_sample. Leaf views: top_functions / get_call_stack /
        query_markers. Counter time series: list_counters / counter_samples.

        Unsure how to proceed? Call get_llm_guide for the full field manual.
        """;

    /// <summary>
    /// Runs the MCP server on stdin/stdout until the client disconnects or <paramref name="cancellationToken"/> is canceled.
    /// </summary>
    /// <param name="selfCommand">How to start ultra for the capture jobs.</param>
    /// <param name="version">The version of ultra.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static async Task RunAsync(UltraSelfCommand selfCommand, string version, CancellationToken cancellationToken)
    {
        UltraJobs.Configure(selfCommand);
        try
        {
            await using var transport = new StdioServerTransport("ultra");
            await using var server = McpServer.Create(transport, CreateOptions(version));
            await server.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Stop the captures still running: they write their profile and exit
            UltraJobs.StopAll();
        }
    }

    /// <summary>
    /// Creates the options of the server: its information, instructions and tools.
    /// </summary>
    /// <param name="version">The version of ultra.</param>
    /// <returns>The options.</returns>
    public static McpServerOptions CreateOptions(string version)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "ultra", Version = version, WebsiteUrl = "https://github.com/xoofx/ultra" },
            ServerInstructions = ServerInstructions,
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(),
        };

        foreach (var tool in CreateTools())
        {
            options.ToolCollection.Add(tool);
        }

        // Strict tool-argument validation: reject any argument key that isn't a declared parameter of the target tool.
        // Without this, typos get silently dropped and the LLM never learns it called the tool wrong.
        // See https://github.com/modelcontextprotocol/csharp-sdk/issues/1508.
        options.Filters.Request.CallToolFilters.Add(next => async (context, cancellationToken) =>
        {
            if (context.MatchedPrimitive is McpServerTool tool &&
                context.Params?.Arguments is { Count: > 0 } arguments &&
                tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object)
            {
                var valid = properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var unknown = arguments.Keys.Where(k => !valid.Contains(k)).ToList();
                if (unknown.Count > 0)
                {
                    throw new McpException(
                        $"Unknown argument(s) for tool '{tool.ProtocolTool.Name}': {string.Join(", ", unknown)}. " +
                        $"Valid arguments: {string.Join(", ", valid.OrderBy(s => s))}.");
                }
            }

            return await next(context, cancellationToken).ConfigureAwait(false);
        });

        return options;
    }

    /// <summary>
    /// Creates the tools: every static method marked with <see cref="McpServerToolAttribute"/> in the tool classes.
    /// </summary>
    /// <returns>The tools.</returns>
    public static List<McpServerTool> CreateTools()
    {
        var tools = new List<McpServerTool>();
        AddTools(typeof(UltraTools), tools);
        AddTools(typeof(UltraHelp), tools);
        AddTools(typeof(UltraJobs), tools);
        return tools;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The tool methods and their parameter types are kept by the DynamicallyAccessedMembers annotation; ultra is not trimmed.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "ultra runs on the JIT (global tool), not NativeAOT.")]
    private static void AddTools([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type, List<McpServerTool> tools)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            {
                tools.Add(McpServerTool.Create(method, (object?)null));
            }
        }
    }
}
