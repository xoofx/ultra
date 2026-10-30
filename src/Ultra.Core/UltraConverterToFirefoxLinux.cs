// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing;
using Ultra.Core.Elf;
using Ultra.Core.Linux;
using Ultra.Core.Model;
using Ultra.Core.Symbols;

namespace Ultra.Core;

/// <summary>
/// Converts a Linux trace (a .ultraperf file of perf events plus the CLR .nettrace file of each .NET process)
/// to a Firefox profile.
/// </summary>
internal sealed partial class UltraConverterToFirefoxLinux : UltraConverterToFirefoxUTrace
{
    private readonly string _perfFilePath;
    private readonly Dictionary<int, string> _clrFilePaths = new();
    private readonly Dictionary<int, double> _clrTimeOffsetsInMs = new();
    private readonly List<EventPipeEventSource> _clrSources = new();
    private UltraPerfTraceLoader? _loader;

    public UltraConverterToFirefoxLinux(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options) : base(traceFiles, options)
    {
        string? perfFilePath = null;
        foreach (var traceFile in traceFiles)
        {
            if (traceFile.IsUltraPerf)
            {
                if (perfFilePath is not null) throw new ArgumentException("Only one .ultraperf file is supported");
                perfFilePath = traceFile.FileName;
            }
            else if (traceFile.IsNettrace)
            {
                var match = ClrFileRegex().Match(Path.GetFileName(traceFile.FileName));
                if (!match.Success) throw new ArgumentException($"Unrecognized nettrace file name `{traceFile.FileName}`. Expecting the CLR file of a process: `<name>_<pid>_clr.nettrace`");
                _clrFilePaths[int.Parse(match.Groups[1].Value)] = traceFile.FileName;
            }
            else
            {
                throw new ArgumentException($"Unsupported trace file `{traceFile.FileName}` with a .ultraperf file");
            }
        }

        _perfFilePath = perfFilePath ?? throw new ArgumentException("A .ultraperf file is required");
    }

    [GeneratedRegex(@"_(\d+)_clr\.nettrace$", RegexOptions.IgnoreCase)]
    private static partial Regex ClrFileRegex();

    /// <inheritdoc />
    public override void Dispose()
    {
        foreach (var source in _clrSources) source.Dispose();
        _clrSources.Clear();
        _loader?.Dispose();
        _loader = null;
    }

    /// <inheritdoc />
    protected override UTraceSession LoadSession(List<int> processIds)
    {
        _loader = new UltraPerfTraceLoader(_perfFilePath);
        var session = new UTraceSession();

        // The CLR file of a process is found next to the perf file when not given
        var baseName = Path.Combine(Path.GetDirectoryName(_perfFilePath) ?? string.Empty, Path.GetFileNameWithoutExtension(_perfFilePath));
        foreach (var pid in processIds)
        {
            if (!_clrFilePaths.ContainsKey(pid) && File.Exists($"{baseName}_{pid}_clr.nettrace"))
            {
                _clrFilePaths[pid] = $"{baseName}_{pid}_clr.nettrace";
            }
        }

        // The managed methods must be known before the samples are loaded (an address is resolved at the sample time)
        foreach (var pid in processIds)
        {
            if (!_clrFilePaths.TryGetValue(pid, out var clrFilePath)) continue;

            Options.LogProgress?.Invoke($"Loading CLR events of process {pid}");
            var clrSource = new EventPipeEventSource(clrFilePath);
            _clrSources.Add(clrSource);
            new UltraEventProcessor(session, clrSource).Run();
            _clrTimeOffsetsInMs[pid] = GetClrTimeOffsetInMs(clrSource, _loader);
        }

        Options.LogProgress?.Invoke("Loading perf events 0%");
        _loader.Load(session, processIds, pid => _clrTimeOffsetsInMs.GetValueOrDefault(pid), Options.LogProgress);
        if (_loader.LostRecords > 0)
        {
            Options.LogProgress?.Invoke($"Warning: the kernel lost {_loader.LostRecords} perf records while recording");
        }

        return session;
    }

    // TraceEvent keeps the QPC clock of a session internal: the frequency and the QPC value of the session start
    internal static readonly FieldInfo? QpcFreqField = typeof(TraceEventSource).GetField("_QPCFreq", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static readonly FieldInfo? SessionStartTimeQpcField = typeof(TraceEventSource).GetField("sessionStartTimeQPC", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// The offset in milliseconds between the start of the CLR session (the zero of its relative timestamps) and the
    /// start of the perf trace. Both clocks are CLOCK_MONOTONIC on Linux (the runtime's QPC is in nanoseconds), so the
    /// offset is exact; the UTC times are the fallback.
    /// </summary>
    internal static double GetClrTimeOffsetInMs(EventPipeEventSource clrSource, UltraPerfTraceLoader loader)
    {
        if (QpcFreqField?.GetValue(clrSource) is long qpcFreq && qpcFreq == 1_000_000_000L
            && SessionStartTimeQpcField?.GetValue(clrSource) is long sessionStartTimeQpc && sessionStartTimeQpc > 0)
        {
            return (sessionStartTimeQpc - (double)loader.Header.StartMonotonicNs) / 1_000_000.0;
        }

        return (clrSource.SessionStartTime.ToUniversalTime() - loader.StartTimeUtc).TotalMilliseconds;
    }

    /// <inheritdoc />
    protected override double GetClrTimeOffsetInMs(UTraceProcess process) => _clrTimeOffsetsInMs.GetValueOrDefault(process.ProcessID);

    /// <inheritdoc />
    protected override INativeSymbolResolver? CreateSymbolResolver(UTraceModuleFile moduleFile)
    {
        if (moduleFile.FilePath == KernelSymbolResolver.ModulePath)
        {
            return _loader?.KernelSymbols;
        }

        return ElfSymbolReader.TryRead(moduleFile.FilePath, out var reader) ? reader : null;
    }
}
