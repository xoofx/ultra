// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Symbols;

namespace Ultra.Core;

public class EtwUltraProfilerOptions
{
    public EtwUltraProfilerOptions()
    {
        CheckDeltaTimeInMs = 100;
        UpdateLogAfterInMs = 1000;
        TimeOutAfterInMs = 30 * 1000; // 30s
        CpuSamplingIntervalInMs = 1000.0f / 8190.0f;
        KeepMergedEtl = false;
        KeepEtlIntermediateFiles = false;
    }

    public List<int> ProcessIds { get; } = new();

    public string? ProgramPath { get; set; }

    public List<string> Arguments { get; } = new();

    public int CheckDeltaTimeInMs { get; set; }

    public int UpdateLogAfterInMs { get; set; }
    
    public int TimeOutAfterInMs { get; set; }

    public Action<string>? LogProgress;

    public Action<string>? LogStepProgress;

    public Action<string>? WaitingFileToComplete;

    public Action<string>? WaitingFileToCompleteTimeOut;

    public bool KeepEtlIntermediateFiles { get; set; }

    public bool KeepMergedEtl { get; set; }

    public float CpuSamplingIntervalInMs { get; set; }

    public string? SymbolPathText { get; set; }

    public SymbolPath GetCachedSymbolPath()
    {
        var symbolPath = new SymbolPath();
        if (SymbolPathText != null)
        {
            symbolPath.Add(SymbolPathText);
        }
        else
        {
            var symbolPathText = SymbolPath.SymbolPathFromEnvironment;
            if (string.IsNullOrEmpty(symbolPathText))
            {
                symbolPathText = $"{SymbolPath.MicrosoftSymbolServerPath};SRV*https://symbols.nuget.org/download/symbols";
            }
            
            symbolPath.Add(symbolPathText);
        }

        return symbolPath.InsureHasCache(symbolPath.DefaultSymbolCache()).CacheFirst();
    }
}