// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;

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
        UltraSamplerProfilerState? profilerState = null;

        var runner = new ProfilerRunner(baseName)
        {
            OnStart = () => Task.CompletedTask,

            OnProfiling = () => profilerState!.TotalFileLength(),

            OnPrepareStartProcess = (processInfo) =>
            {
                SetupUltraSampler(processInfo);
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

            OnFinalCleanup = () => Task.CompletedTask,

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


    private static void SetupUltraSampler(ProcessStartInfo startInfo)
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
            await _samplerSession.WaitForConnectAndStartSession();

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