// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Core;

/// <summary>
/// Converts a list of trace files (one ETL file or a list of nettrace files) to a Firefox profile.
/// </summary>
public abstract class UltraConverterToFirefox : IDisposable
{
    private protected readonly List<UltraProfilerTraceFile> TraceFiles;
    private protected readonly UltraProfilerOptions Options;
    private protected FirefoxProfiler.Profile ProfilerResult;

    /// <summary>
    /// A generic other category.
    /// </summary>
    public const int CategoryOther = 0;

    /// <summary>
    /// The kernel category.
    /// </summary>
    public const int CategoryKernel = 1;

    /// <summary>
    /// The native category.
    /// </summary>
    public const int CategoryNative = 2;

    /// <summary>
    /// The managed category.
    /// </summary>
    public const int CategoryManaged = 3;

    /// <summary>
    /// The GC category.
    /// </summary>
    public const int CategoryGc = 4;

    /// <summary>
    /// The JIT category.
    /// </summary>
    public const int CategoryJit = 5;

    /// <summary>
    /// The CLR category.
    /// </summary>
    public const int CategoryClr = 6;

    private protected UltraConverterToFirefox(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options)
    {
        TraceFiles = traceFiles;
        this.Options = options;
        ProfilerResult = new FirefoxProfiler.Profile(); // Create an empty profile (not used and override by the derived class)
    }

    /// <inheritdoc />
    public abstract void Dispose();

    /// <summary>
    /// Converts a list of trace files (one ETL file, or a list of nettrace files) to a Firefox profile.
    /// </summary>
    /// <param name="traceFiles">The list of trace files to convert.</param>
    /// <param name="options">The options used for converting.</param>
    /// <param name="processIds">The list of process ids to extract from the trace files.</param>
    /// <returns>The converted Firefox profile.</returns>
    public static FirefoxProfiler.Profile Convert(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options, List<int> processIds)
    {
        traceFiles = PrepareTraceFiles(traceFiles, options, processIds);
        using var converter = CreateConverter(traceFiles, options);
        return converter.Convert(processIds);
    }

    /// <summary>
    /// Converts a list of trace files (one or more ETL files, or a list of nettrace files) to one Firefox profile per process.
    /// </summary>
    /// <param name="traceFiles">The list of trace files to convert.</param>
    /// <param name="options">The options used for converting.</param>
    /// <param name="processIds">The list of process ids to extract from the trace files.</param>
    /// <returns>The converted Firefox profile of each process, converted one at a time while enumerating.</returns>
    public static IEnumerable<(int ProcessId, FirefoxProfiler.Profile Profile)> ConvertEachProcess(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options, List<int> processIds)
    {
        // The ETL files are filtered once for all the processes
        traceFiles = PrepareTraceFiles(traceFiles, options, processIds);
        foreach (var processId in processIds)
        {
            using var converter = CreateConverter(traceFiles, options);
            yield return (processId, converter.Convert([processId]));
        }
    }

    private static List<UltraProfilerTraceFile> PrepareTraceFiles(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options, List<int> processIds)
    {
        var extension = GetExtension(traceFiles);
        if (extension == ".etl")
        {
            if (options.FastFilter)
            {
                // Several ETL files (e.g. the unmerged kernel, user and rundown files of a profile) are read as one trace
                return [new UltraProfilerTraceFile(UltraEtwFastFilter.Filter(traceFiles.Select(x => x.FileName).ToList(), processIds, options))];
            }

            if (traceFiles.Count > 1)
            {
                throw new ArgumentException("Only one ETL file is supported with --nofilter");
            }
        }

        return traceFiles;
    }

    private static UltraConverterToFirefox CreateConverter(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options)
    {
        var extension = GetExtension(traceFiles);
        if (extension == ".etl" || extension == ".etlx")
        {
            return new UltraConverterToFirefoxEtw(traceFiles, options);
        }
        else if (extension == ".nettrace")
        {
            return new UltraConverterToFirefoxEventPipe(traceFiles, options);
        }
        else if (extension == ".ultraperf")
        {
            return new UltraConverterToFirefoxLinux(traceFiles, options);
        }
        else
        {
            throw new ArgumentException($"Unsupported trace file extension [{extension}]");
        }
    }

    private static string GetExtension(List<UltraProfilerTraceFile> traceFiles)
    {
        var extensions = traceFiles.Select(x => Path.GetExtension(x.FileName).ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        // A Linux profile is a .ultraperf file plus the CLR .nettrace file of each .NET process
        if (extensions.Contains(".ultraperf"))
        {
            extensions.Remove(".nettrace");
        }

        if (extensions.Count != 1)
        {
            throw new ArgumentException($"All trace files must have the same extension. Instead got [{string.Join(", ", extensions)}]");
        }

        return extensions.First();
    }

    private FirefoxProfiler.Profile Convert(List<int> processIds)
    {
        ConvertImpl(processIds);
        return ProfilerResult;
    }

    private protected abstract void ConvertImpl(List<int> processIds);
}
