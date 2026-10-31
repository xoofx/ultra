// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;

namespace Ultra;

/// <summary>
/// Stops profiling from outside the console: when a stop file appears (`--stop-file`, or the first Ctrl+C relayed by the
/// non-elevated ultra of `--elevate`), when that non-elevated ultra exits, or aborts when an abort file appears.
/// </summary>
internal sealed class UltraStopMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private readonly List<string> _stopFiles;
    private readonly string? _abortFile;
    private readonly Process? _parentProcess;
    private readonly Action<string> _onStop;
    private readonly Action _onAbort;
    private readonly Timer _timer;
    private int _stopped;
    private int _aborted;

    private UltraStopMonitor(List<string> stopFiles, string? abortFile, Process? parentProcess, Action<string> onStop, Action onAbort)
    {
        _stopFiles = stopFiles;
        _abortFile = abortFile;
        _parentProcess = parentProcess;
        _onStop = onStop;
        _onAbort = onAbort;
        _timer = new Timer(_ => Poll(), null, PollInterval, PollInterval);
    }

    /// <summary>
    /// Starts monitoring.
    /// </summary>
    /// <param name="stopFile">The `--stop-file`, deleted now if it exists from a previous run.</param>
    /// <param name="child">The elevated side of `--elevate`, when this ultra is elevated by another one.</param>
    /// <param name="onStop">Called once with the reason to display (empty when already displayed) when profiling must stop (the profile is still written).</param>
    /// <param name="onAbort">Called once when profiling must abort.</param>
    /// <returns>The monitor, or <c>null</c> when there is nothing to monitor.</returns>
    public static UltraStopMonitor? Start(string? stopFile, UltraElevationChild? child, Action<string> onStop, Action onAbort)
    {
        var stopFiles = new List<string>();
        if (!string.IsNullOrEmpty(stopFile))
        {
            stopFile = Path.GetFullPath(stopFile);
            if (File.Exists(stopFile))
            {
                File.Delete(stopFile);
            }

            stopFiles.Add(stopFile);
        }

        Process? parentProcess = null;
        if (child is not null)
        {
            stopFiles.Add(child.StopFile);
            try
            {
                parentProcess = Process.GetProcessById(child.ParentProcessId);
            }
            catch (ArgumentException)
            {
                // Already exited: the first poll stops
            }
        }

        if (stopFiles.Count == 0)
        {
            return null;
        }

        return new UltraStopMonitor(stopFiles, child?.AbortFile, parentProcess, onStop, onAbort);
    }

    private void Poll()
    {
        if (_abortFile is not null && File.Exists(_abortFile) && Interlocked.Exchange(ref _aborted, 1) == 0)
        {
            _onAbort();
            return;
        }

        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        string? reason = null;
        for (var i = 0; i < _stopFiles.Count; i++)
        {
            if (File.Exists(_stopFiles[i]))
            {
                // The stop file of the elevation channel is the Ctrl+C of the parent, which already said it
                reason = _abortFile is not null && i == _stopFiles.Count - 1 ? string.Empty : $"Stop requested by the stop file {_stopFiles[i]}: the profile is still written";
                break;
            }
        }

        if (reason is null && _parentProcess is { HasExited: true })
        {
            reason = "Stop requested: the ultra that started this elevated ultra has exited, the profile is still written";
        }

        if (reason is not null && Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _onStop(reason);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _parentProcess?.Dispose();
    }
}
