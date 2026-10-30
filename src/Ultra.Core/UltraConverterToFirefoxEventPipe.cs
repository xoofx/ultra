// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing;
using Ultra.Core.MachO;
using Ultra.Core.Model;
using Ultra.Core.Symbols;

namespace Ultra.Core;

/// <summary>
/// Converts nettrace files (sampler + CLR EventPipe sessions) to a Firefox profile.
/// </summary>
internal sealed class UltraConverterToFirefoxEventPipe : UltraConverterToFirefoxUTrace
{
    private const string SamplerFileSuffix = "_sampler.nettrace";
    private const string ClrFileSuffix = "_clr.nettrace";

    private readonly EventPipeEventSource _samplerSource;
    private readonly EventPipeEventSource? _clrSource;

    // Offset to apply to CLR relative timestamps to realign them with the sampler session start time
    private double _clrTimeOffsetInMs;

    public UltraConverterToFirefoxEventPipe(List<UltraProfilerTraceFile> traceFiles, UltraProfilerOptions options) : base(traceFiles, options)
    {
        string? samplerFilePath = null;
        string? clrFilePath = null;
        foreach (var traceFile in traceFiles)
        {
            if (traceFile.FileName.EndsWith(SamplerFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                samplerFilePath = traceFile.FileName;
            }
            else if (traceFile.FileName.EndsWith(ClrFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                clrFilePath = traceFile.FileName;
            }
            else if (traceFiles.Count > 1)
            {
                // Don't ignore silently a file that we don't recognize
                throw new ArgumentException($"Unrecognized nettrace file name `{traceFile.FileName}`. Expecting a file ending with `{SamplerFileSuffix}` or `{ClrFileSuffix}`");
            }
        }

        // A single file not following the naming convention is assumed to be the sampler file
        samplerFilePath ??= traceFiles[0].FileName;

        _samplerSource = new EventPipeEventSource(samplerFilePath);
        _clrSource = clrFilePath is not null ? new EventPipeEventSource(clrFilePath) : null;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _samplerSource.Dispose();
        _clrSource?.Dispose();
    }

    /// <inheritdoc />
    protected override UTraceSession LoadSession(List<int> processIds)
    {
        var processor = _clrSource is not null
            ? new UltraEventProcessor(_samplerSource, _clrSource)
            : new UltraEventProcessor(_samplerSource);

        var session = processor.Run();

        // The CLR session and the sampler session don't start at the same time. Sample timestamps are relative to
        // the sampler session start while CLR marker timestamps are relative to the CLR session start.
        _clrTimeOffsetInMs = _clrSource is not null
            ? (_clrSource.SessionStartTime - _samplerSource.SessionStartTime).TotalMilliseconds
            : 0.0;

        return session;
    }

    /// <inheritdoc />
    protected override double GetClrTimeOffsetInMs(UTraceProcess process) => _clrTimeOffsetInMs;

    /// <inheritdoc />
    protected override INativeSymbolResolver? CreateSymbolResolver(UTraceModuleFile moduleFile)
    {
        return MachOSymbolReader.TryRead(moduleFile.FilePath, out var reader) && reader is not null ? new MachOSymbolResolver(reader) : null;
    }

    /// <inheritdoc />
    protected override string GetLibArch(UTraceProcess process) => "arm64";

    private sealed class MachOSymbolResolver(MachOSymbolReader reader) : INativeSymbolResolver
    {
        public bool TryResolve(UTraceNativeModule module, ulong address, out ulong symbolAddress, out string symbolName)
        {
            if (reader.TryResolve(module.BaseAddress, address, out var symbol))
            {
                symbolAddress = symbol.Address;
                symbolName = symbol.Name;
                return true;
            }

            symbolAddress = 0;
            symbolName = string.Empty;
            return false;
        }
    }
}
