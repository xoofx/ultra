// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net.Sockets;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;
using Ultra.Sampler;

namespace Ultra.Core;

/// <summary>
/// Handles an EventPipe session to a diagnostic port.
/// </summary>
internal class DiagnosticPortSession
{
    private readonly int _pid;
    private readonly bool _sampler;
    private readonly string _baseName;
    private readonly Task _connectTask;
    private readonly SemaphoreSlim _semaphoreSlim;
    private Task? _profilingTask;
    private readonly CancellationTokenSource _cancelConnectSource;
    private DiagnosticsClient? _diagnosticsClient;
    private EventPipeSession? _eventPipeSession;
    private string? _nettraceFilePath;
    private FileStream? _nettraceFileStream;
    private Task? _eventStreamCopyTask;
    private bool _disposed;
    
    public DiagnosticPortSession(int pid, bool sampler, string baseName, CancellationToken token)
    {
        _pid = pid;
        _sampler = sampler;
        _baseName = baseName;
        _cancelConnectSource = new CancellationTokenSource();
        _semaphoreSlim = new SemaphoreSlim(0);
        _connectTask = ConnectAndStartProfilingImpl(pid, sampler, baseName, token);
    }

    private async Task ConnectAndStartProfilingImpl(int pid, bool sampler, string baseName, CancellationToken token)
    {
        CancellationTokenSource linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, _cancelConnectSource.Token);
        try
        {
            
            var connectCancellationToken = linkedCancellationTokenSource.Token;

            if (sampler)
            {
                _cancelConnectSource.CancelAfter(500);
            }

            var connectionAddress = await TryFindConnectionAddress(pid, sampler, connectCancellationToken).ConfigureAwait(false);
            if (connectionAddress is null) return;

            _diagnosticsClient = (await DiagnosticsClientConnector.FromDiagnosticPort(connectionAddress, connectCancellationToken).ConfigureAwait(false))?.Instance;
            if (_diagnosticsClient is null) return;

            await _diagnosticsClient.WaitForConnectionAsync(connectCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            if (sampler && _cancelConnectSource is not null && _cancelConnectSource.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Cannot connect to the diagnostic port socket for pid {pid}", ex);
            }
            return;
        }
        finally
        {
            linkedCancellationTokenSource.Dispose();
        }
    }

    public async Task StartProfiling(CancellationToken token)
    {
        // We want to make sure that we are not disposing while we are connecting
        await _semaphoreSlim.WaitAsync(token);

        try
        {
            if (_disposed)
            {
                return;
            }

            _profilingTask = _connectTask.ContinueWith(async task =>
            {


                _nettraceFilePath = Path.Combine(Environment.CurrentDirectory, $"{_baseName}_{(_sampler ? "sampler" : "main")}_{_pid}.nettrace");
                _nettraceFileStream = new FileStream(_nettraceFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);

                long keywords = -1;
                var providerName = UltraSamplerParser.Name;
                var level = EventLevel.Verbose;

                if (!_sampler)
                {
                    providerName = "Microsoft-Windows-DotNETRuntime";
                    keywords = (long)(
                        ClrTraceEventParser.Keywords.JITSymbols |
                        ClrTraceEventParser.Keywords.Exception |
                        ClrTraceEventParser.Keywords.GC |
                        ClrTraceEventParser.Keywords.GCHeapAndTypeNames |
                        ClrTraceEventParser.Keywords.Interop |
                        ClrTraceEventParser.Keywords.JITSymbols |
                        ClrTraceEventParser.Keywords.Jit |
                        ClrTraceEventParser.Keywords.JittedMethodILToNativeMap |
                        ClrTraceEventParser.Keywords.Loader |
                        ClrTraceEventParser.Keywords.Stack |
                        ClrTraceEventParser.Keywords.StartEnumeration
                    );
                }

                var ultraEventProvider = new EventPipeProvider(providerName, level, (long)keywords);
                var config = new EventPipeSessionConfiguration([ultraEventProvider], 512, !_sampler, true);
                _eventPipeSession = await _diagnosticsClient!.StartEventPipeSessionAsync(config, token).ConfigureAwait(false);
                _eventStreamCopyTask = _eventPipeSession.EventStream.CopyToAsync(_nettraceFileStream, token);
            }, token);
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    public long GetNettraceFileLength() => _nettraceFileStream?.Length ?? 0;

    public async Task WaitForConnectAndStartSession()
    {
        await _connectTask.ConfigureAwait(false);
    }
    
    private static async Task<string?> TryFindConnectionAddress(int pid, bool sampler, CancellationToken token)
    {
        var tempFolder = Path.GetTempPath();
        tempFolder = sampler ? Path.Combine(tempFolder, ".ultra") : tempFolder;

        var pattern = $"dotnet-diagnostic-{pid}-*-socket";
        string? diagnosticPortSocket = null;

        while (true)
        {
            if (Directory.Exists(tempFolder))
            {
                DateTime lastWriteTime = default;
                foreach (var file in Directory.EnumerateFiles(tempFolder, pattern))
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTime > lastWriteTime)
                    {
                        diagnosticPortSocket = file;
                        lastWriteTime = fileInfo.LastWriteTime;
                    }
                }

                if (diagnosticPortSocket != null)
                {
                    // Force connect mode
                    diagnosticPortSocket = $"{diagnosticPortSocket},connect";
                    break;
                }
            }

            await Task.Delay(10, token).ConfigureAwait(false);
        }

        return diagnosticPortSocket;
    }

    public async ValueTask StopAndDisposeAsync()
    {
        // We want to make sure that we are not disposing while we are connecting
        await _semaphoreSlim.WaitAsync(CancellationToken.None);

        try
        {
            if (_profilingTask is null)
            {
                // We cancel any pending connection
                await _cancelConnectSource.CancelAsync();
            }
            else
            {
                try
                {
                    // We wait for the session to start (we will close it right after below
                    await _profilingTask.ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
            }

            Debug.Assert(_eventStreamCopyTask is not null);
            try
            {
                await _eventStreamCopyTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }

            Debug.Assert(_nettraceFileStream is not null);
            try
            {
                await _nettraceFileStream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }

            Debug.Assert(_eventPipeSession is not null);
            try
            {
                await _eventPipeSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore
            }
            finally
            {
                try
                {
                    _eventPipeSession.Dispose();
                }
                catch
                {
                    // Ignore
                }
            }
        }
        finally
        {
            _disposed = true;
            _semaphoreSlim.Release();

            _cancelConnectSource.Dispose();
        }
    }

    private async Task StopAsync(CancellationToken token)
    {
        if (_eventPipeSession is null) return;

        try
        {
            await _eventPipeSession.StopAsync(token).ConfigureAwait(false);
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
}