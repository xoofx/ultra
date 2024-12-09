// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO.Enumeration;
using System.Net.Sockets;
using ByteSizeLib;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;
using Ultra.Sampler;

namespace Ultra.Core;

/// <summary>
/// A profiler that uses Event Tracing for Windows (ETW) to collect performance data.
/// </summary>
internal sealed class UltraProfilerEventPipe : UltraProfiler
{
    private static string PathToNativeUltraSampler => Path.Combine(AppContext.BaseDirectory, "libUltraSamplerIndirect.dyld");

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

            OnProcessStarted = async (process) =>
            {
                profilerState = await UltraSamplerProfilerState.Connect(baseName, process.Id, CancellationToken, ultraProfilerOptions);
            },

            OnStop = async () =>
            {
                try
                {
                    await profilerState!.Stop();
                }
                finally
                {
                    // Force dispose
                    profilerState!.Dispose();
                }
            },

            OnCatch = () =>
            {
                return Task.CompletedTask;
            },

            OnFinally = () =>
            {
                return Task.CompletedTask;
            },

            FinishFileToConvert = () =>
            {
                // TODO
                return Task.FromResult(string.Empty);
            },

            OnFinalCleanup = () =>
            {
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

    private class UltraSamplerProfilerState : IDisposable
    {
        private readonly UltraProfilerOptions _options;
        private readonly CancellationToken _token;

        private readonly DiagnosticsClient _ultraDiagnosticsClient;
        private EventPipeSession? _ultraSession;
        private readonly string _ultraNetTraceFilePath;
        private readonly FileStream _ultraNetTraceFileStream;
        private Task? _ultraEventStreamCopyTask;

        private readonly DiagnosticsClient? _mainDiagnosticsClient;
        private EventPipeSession? _mainSession;
        private readonly string? _mainNetTraceFilePath;
        private readonly FileStream? _mainNetTraceFileStream;
        private Task? _mainEventStreamCopyTask;

        private UltraSamplerProfilerState(UltraProfilerOptions options, DiagnosticsClient? mainDiagnosticsClient, DiagnosticsClient ultraDiagnosticsClient, string baseName, int pid, CancellationToken token)
        {
            _options = options;
            _token = token;
            _ultraDiagnosticsClient = ultraDiagnosticsClient;
            _ultraNetTraceFilePath = Path.Combine(Environment.CurrentDirectory, $"{baseName}_sampler_{pid}.nettrace");
            _ultraNetTraceFileStream = new FileStream(_ultraNetTraceFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (mainDiagnosticsClient != null)
            {
                _mainDiagnosticsClient = mainDiagnosticsClient;
                _mainNetTraceFilePath = Path.Combine(Environment.CurrentDirectory, $"{baseName}_main_{pid}.nettrace");
                _mainNetTraceFileStream = new FileStream(_mainNetTraceFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
            }
        }

        public static async Task<UltraSamplerProfilerState> Connect(string baseName, int pid, CancellationToken token, UltraProfilerOptions options)
        {

            //var ultraTempFolder = Path.Combine(Path.GetTempPath(), ".ultra");
            var ultraTempFolder = Path.Combine(Path.GetTempPath(), ".ultra");

            var pattern = $"dotnet-diagnostic-{pid}-*";
            string? ultraDiagnosticPortSocket = null;

            var stopWatch = Stopwatch.StartNew();
            while (true)
            {
                if (stopWatch.Elapsed.TotalSeconds > 1.0)
                {
                    throw new InvalidOperationException("Cannot find the diagnostic port socket");
                }

                if (Directory.Exists(ultraTempFolder))
                {
                    foreach (var file in Directory.EnumerateFiles(ultraTempFolder, pattern))
                    {
                        ultraDiagnosticPortSocket = file;
                        break;
                    }

                    if (ultraDiagnosticPortSocket is not null)
                    {
                        // Force connect mode
                        ultraDiagnosticPortSocket = $"{ultraDiagnosticPortSocket},connect";
                        break;
                    }
                }

                await Task.Delay(5, token);
            }

            var diagnosticClientMain = new DiagnosticsClient(pid);
            //DiagnosticsClient? diagnosticClientMain = null;
            var diagnosticClientUltra = (await DiagnosticsClientConnector.FromDiagnosticPort(ultraDiagnosticPortSocket, token).ConfigureAwait(false))!.Instance;

            var timeoutSource = new CancellationTokenSource();
            var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutSource.Token);

            try
            {
                timeoutSource.CancelAfter(1000);
                await diagnosticClientUltra!.WaitForConnectionAsync(linkedCancellationTokenSource.Token).ConfigureAwait(false);
                await diagnosticClientMain.WaitForConnectionAsync(linkedCancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                throw new InvalidOperationException("Cannot connect to the diagnostic port socket");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw;
            }

            return new UltraSamplerProfilerState(options, diagnosticClientMain, diagnosticClientUltra, baseName, pid, token);
        }

        public long TotalFileLength()
        {
            long totalLength = 0;
            totalLength += _ultraNetTraceFileStream.Length;

            if (_mainNetTraceFileStream is not null)
            {
                totalLength += _mainNetTraceFileStream.Length;
            }

            return totalLength;
        }

        public async Task StartProfiling()
        {
            var ultraEventProvider = new EventPipeProvider(UltraSamplerParser.Name, EventLevel.Verbose);
            var config = new EventPipeSessionConfiguration([ultraEventProvider], 256, false, true);
            _ultraSession = await _ultraDiagnosticsClient.StartEventPipeSessionAsync(config, _token).ConfigureAwait(false);
            _ultraEventStreamCopyTask = _ultraSession.EventStream.CopyToAsync(_ultraNetTraceFileStream, _token);

            if (_mainDiagnosticsClient is not null)
            {
                var jitEvents = ClrTraceEventParser.Keywords.JITSymbols |
                                ClrTraceEventParser.Keywords.Exception |
                                ClrTraceEventParser.Keywords.GC |
                                ClrTraceEventParser.Keywords.GCHeapAndTypeNames |
                                ClrTraceEventParser.Keywords.Interop |
                                ClrTraceEventParser.Keywords.JITSymbols |
                                ClrTraceEventParser.Keywords.Jit |
                                ClrTraceEventParser.Keywords.JittedMethodILToNativeMap |
                                ClrTraceEventParser.Keywords.Loader |
                                ClrTraceEventParser.Keywords.Stack |
                                ClrTraceEventParser.Keywords.StartEnumeration;

                var mainEventProvider = new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, (long)jitEvents);

                _mainSession = await _mainDiagnosticsClient.StartEventPipeSessionAsync([mainEventProvider], true, 256, _token).ConfigureAwait(false);
                _mainEventStreamCopyTask = _mainSession.EventStream.CopyToAsync(_mainNetTraceFileStream!, _token);
            }
        }

        public async Task Stop()
        {
            if (_ultraEventStreamCopyTask is not null)
            {
                await _ultraEventStreamCopyTask.ConfigureAwait(false);
            }

            if (_mainEventStreamCopyTask is not null)
            {
                await _mainEventStreamCopyTask.ConfigureAwait(false);
            }

            await SafeStopAsync(_ultraSession, _token);
            _ultraSession = null;

            await SafeStopAsync(_mainSession, _token);
            _mainSession = null;
        }

        private static async Task SafeStopAsync(EventPipeSession? session, CancellationToken token)
        {
            if (session is null) return;

            try
            {
                await session.StopAsync(token).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {

            }
            catch (TimeoutException)
            {

            }
            catch (OperationCanceledException)
            {

            }
            catch (PlatformNotSupportedException)
            {

            }
            catch (ServerNotAvailableException)
            {

            }
            catch (SocketException)
            {

            }
        }

        public void Dispose()
        {
            _ultraSession?.Dispose();
            _mainSession?.Dispose();
            _ultraNetTraceFileStream.Dispose();

            if (_mainNetTraceFileStream is not null)
            {
                _mainNetTraceFileStream.Dispose();
            }
        }
    }

}