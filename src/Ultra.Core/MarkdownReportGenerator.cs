// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Ultra.Core.Markers;

namespace Ultra.Core;

/// <summary>
/// Generates a markdown report from a Firefox profile.
/// </summary>
internal sealed class MarkdownReportGenerator
{
    private readonly FirefoxProfiler.Profile _profile;
    private readonly StreamWriter _writer;

    private MarkdownReportGenerator(FirefoxProfiler.Profile profile, StreamWriter writer)
    {
        _profile = profile;
        _writer = writer;
    }

    /// <summary>
    /// Generates a markdown report from a Firefox profile.
    /// </summary>
    /// <param name="profile">The Firefox profile.</param>
    /// <param name="writer">The writer to write the markdown report.</param>
    public static void Generate(FirefoxProfiler.Profile profile, StreamWriter writer)
    {
        var generator = new MarkdownReportGenerator(profile, writer);
        generator.Generate();
    }

    private void Generate()
    {
        var pidAndNameList = new HashSet<ProcessInfo>(_profile.Threads.Select(x => new ProcessInfo(x.Pid, x.ProcessName)));

        _writer.WriteLine($"# Ultra Report for \\[{string.Join(", ", pidAndNameList.Select(x => x.Name))}]");
        _writer.WriteLine();
        _writer.WriteLine($"_Generated on {DateTime.Now:s}_");
        _writer.WriteLine();

        foreach (var pidAndName in pidAndNameList)
        {
            var threads = _profile.Threads.Where(x => string.Equals(x.Pid, pidAndName.Pid, StringComparison.OrdinalIgnoreCase)).ToList();
            GenerateProcess(pidAndName, threads);
        }
    }

    private void GenerateProcess(ProcessInfo processInfo, List<FirefoxProfiler.Thread> threads)
    {
        _writer.WriteLine($"## Process {processInfo.Name}");
        _writer.WriteLine();

        GenerateJit(threads);

        _writer.WriteLine();
        _writer.WriteLine("_Report generated by [ultra](https://github.com/xoofx/ultra)_");
    }

    private void GenerateJit(List<FirefoxProfiler.Thread> threads)
    {
        var jitEvents = CollectMarkersFromThreads<JitCompileEvent>(threads, EtwConverterToFirefox.CategoryJit);

        if (jitEvents.Count == 0)
        {
            return;
        }

        double totalTime = 0.0;
        long totalILSize = 0;

        Dictionary<string, (double DurationInMs, long ILSize, int MethodCount)> namespaceStats = new(StringComparer.Ordinal);

        // Sort by duration descending
        jitEvents.Sort((left, right) => right.DurationInMs.CompareTo(left.DurationInMs));

        foreach (var jitEvent in jitEvents)
        {
            totalTime += jitEvent.DurationInMs;
            totalILSize += jitEvent.Data.MethodILSize;

            var ns = GetNamespace(jitEvent.Data.MethodNamespace);
            var indexOfLastDot = ns.LastIndexOf('.');
            ns = indexOfLastDot > 0 ? ns.Substring(0, indexOfLastDot) : "<no namespace>";
            
            if (!namespaceStats.TryGetValue(ns, out var stats))
            {
                stats = (0, 0, 0);
            }

            stats.DurationInMs += jitEvent.DurationInMs;
            stats.ILSize += jitEvent.Data.MethodILSize;
            stats.MethodCount++;

            namespaceStats[ns] = stats;
        }

        _writer.WriteLine("### JIT Statistics");
        _writer.WriteLine();
        
        _writer.WriteLine($"- Total JIT time: `{totalTime:0.0}ms`");
        _writer.WriteLine($"- Total JIT IL size: `{totalILSize}`");

        _writer.WriteLine();
        _writer.WriteLine("#### JIT Top 10 Namespaces");
        _writer.WriteLine();

        _writer.WriteLine("| Namespace | Duration (ms) | IL Size| Methods |");
        _writer.WriteLine("|-----------|---------------|--------|-------");
        var cumulativeTotalTime = 0.0;
        foreach (var (namespaceName, stats) in namespaceStats.OrderByDescending(x => x.Value.DurationInMs))
        {
            _writer.WriteLine($"| ``{namespaceName}`` | `{stats.DurationInMs:0.0}` | `{stats.ILSize}` |`{stats.MethodCount}` |");
            cumulativeTotalTime += stats.DurationInMs;
            if (cumulativeTotalTime > totalTime * 0.9)
            {
                break;
            }
        }

        // TODO: Add a report for Generic Namespace arguments to namespace (e.g ``System.Collections.Generic.List`1[MyNamespace.MyClass...]`)
        // MyNamespace.MyClass should be reported as a separate namespace that contributes to System.Collections

        _writer.WriteLine();
        _writer.WriteLine("#### JIT Top 10 Methods");
        _writer.WriteLine();
        _writer.WriteLine("| Method | Duration (ms) | IL Size");
        _writer.WriteLine("|--------|---------------|--------|");
        foreach (var jitEvent in jitEvents.Take(10))
        {
            _writer.WriteLine($"| ``{jitEvent.Data.FullName}`` | `{jitEvent.DurationInMs:0.0}` | `{jitEvent.Data.MethodILSize}` |");
        }
    }

    private static List<PayloadEvent<TPayload>> CollectMarkersFromThreads<TPayload>(List<FirefoxProfiler.Thread> threads, int category) where TPayload: FirefoxProfiler.MarkerPayload
    {
        var markers = new List<PayloadEvent<TPayload>>();
        foreach (var thread in threads)
        {
            var threadMarkers = thread.Markers;
            var markerLength = threadMarkers.Length;
            for (int i = 0; i < markerLength; i++)
            {
                if (threadMarkers.Category[i] == category)
                {
                    var payload = (TPayload) threadMarkers.Data[i]!;
                    var duration = threadMarkers.EndTime[i]!.Value - threadMarkers.StartTime[i]!.Value;
                    markers.Add(new(payload, duration));
                }
            }
        }
        return markers;
    }

    private static string GetNamespace(string fullTypeName)
    {
        var index = fullTypeName.IndexOf('`'); // For generics
        if (index > 0)
        {
            fullTypeName = fullTypeName.Substring(0, index);
        }
        index = fullTypeName.LastIndexOf('.');
        return index > 0 ? fullTypeName.Substring(0, index) : "<no namespace>";
    }

    private record struct ProcessInfo(string Pid, string? Name);

    private record struct PayloadEvent<TPayload>(TPayload Data, double DurationInMs) where TPayload : FirefoxProfiler.MarkerPayload;
}