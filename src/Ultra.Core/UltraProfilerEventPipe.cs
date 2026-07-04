// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using Ultra.Sampler;

namespace Ultra.Core;

/// <summary>
/// A profiler that uses Event Tracing for Windows (ETW) to collect performance data.
/// </summary>
internal sealed class UltraProfilerEventPipe : UltraProfiler
{
    private static string PathToNativeUltraSampler => Path.Combine(AppContext.BaseDirectory, "libUltraSamplerHook.dylib");

    /// <summary>
    /// Initializes a new instance of the <see cref="UltraProfiler"/> class.
    /// </summary>
    internal UltraProfilerEventPipe()
    {
    }

    private protected override void DisposeImpl()
    {
    }

    private protected override ProfilerRunner CreateRunner(UltraProfilerOptions ultraProfilerOptions, List<Process> processList, string baseName, Process? singleProcess)
    {
        if (processList.Count > 0)
        {
            // The sampler library can only be injected at process startup (via DYLD_INSERT_LIBRARIES)
            throw new PlatformNotSupportedException("Attaching to a running process is not supported on macOS. Use `ultra profile -- <program> [args...]` to launch and profile a program.");
        }

        UltraSamplerProfilerState? profilerState = null;

        var runner = new ProfilerRunner(baseName)
        {
            OnStart = () => Task.CompletedTask,

            OnProfiling = () => profilerState!.TotalFileLength(),

            OnPrepareStartProcess = (processInfo) =>
            {
                SetupUltraSampler(processInfo, ultraProfilerOptions);
                return Task.CompletedTask;
            },

            OnProcessStarted = (process) =>
            {
                profilerState = new UltraSamplerProfilerState(baseName, process.Id, CancellationToken);
                return Task.CompletedTask;
            },

            OnStop = async () =>
            {
                if (profilerState is not null)
                {
                    await profilerState.StopAndDisposeAsync();
                }
            },

            OnCatch = () => Task.CompletedTask,

            OnFinally = () => Task.CompletedTask,

            FinishFileToConvert = () => Task.FromResult(profilerState?.GetGeneratedTraceFiles() ?? []),

            OnFinalCleanup = () =>
            {
                if (!ultraProfilerOptions.KeepMergedEtl && profilerState is not null)
                {
                    foreach (var traceFile in profilerState.GetGeneratedTraceFiles())
                    {
                        File.Delete(traceFile.FileName);
                    }
                }
                return Task.CompletedTask;
            },

            OnEnablingProfiling = async () =>
            {
                if (profilerState is not null)
                {
                    await profilerState.StartProfiling();
                }
            }
        };

        return runner;
    }


    private static void SetupUltraSampler(ProcessStartInfo startInfo, UltraProfilerOptions ultraProfilerOptions)
    {
        const string key = "DYLD_INSERT_LIBRARIES";
        startInfo.Environment.TryGetValue(key, out var value);

        var ultraSamplerPath = PathToNativeUltraSampler;

        if (!string.IsNullOrEmpty(value))
        {
            value += $",{ultraSamplerPath}";
        }
        else
        {
            value = ultraSamplerPath;
        }

        //Console.WriteLine($"DYLD_INSERT_LIBRARIES={value}");
        startInfo.Environment[key] = value;

        // The sampler cannot sleep less than 1ms between samples
        var samplingIntervalInMs = (int)Math.Clamp(MathF.Round(ultraProfilerOptions.CpuSamplingIntervalInMs), 1, 1000);
        startInfo.Environment[UltraSamplerConstants.SamplingIntervalEnvironmentVariable] = samplingIntervalInMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private class UltraSamplerProfilerState
    {
        private readonly CancellationToken _token;
        private readonly DiagnosticPortSession _samplerSession;
        private readonly DiagnosticPortSession _clrSession;

        public UltraSamplerProfilerState(string baseName, int pid, CancellationToken token)
        {
            _token = token;
            _samplerSession = new(pid, true, baseName, token);
            _clrSession = new(pid, false, baseName, token);
        }

        public List<UltraProfilerTraceFile> GetGeneratedTraceFiles()
        {
            var files = new List<UltraProfilerTraceFile>();
            if (_samplerSession.TryGetNettraceFilePathIfExists(out var nettraceFilePath))
            {
                files.Add(new UltraProfilerTraceFile(nettraceFilePath));
            }

            if (_clrSession.TryGetNettraceFilePathIfExists(out nettraceFilePath))
            {
                files.Add(new UltraProfilerTraceFile(nettraceFilePath));
            }
            return files;
        }

        public long TotalFileLength()
        {
            return _samplerSession.GetNettraceFileLength() + _clrSession.GetNettraceFileLength();
        }

        public async Task StartProfiling()
        {
            await _samplerSession.WaitForConnect();

            await _samplerSession.StartProfiling(_token);
            await _clrSession.StartProfiling(_token);
        }

        public async ValueTask StopAndDisposeAsync()
        {
            await _samplerSession.StopAndDisposeAsync().ConfigureAwait(false);

            await _clrSession.StopAndDisposeAsync().ConfigureAwait(false);
        }
    }
}