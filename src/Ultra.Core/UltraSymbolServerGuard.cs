// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Globalization;
using System.Net;

namespace Ultra.Core;

/// <summary>
/// Keeps a symbol server that does not answer from holding up a conversion: the HTTP handler of TraceEvent's SymbolReader.
/// <para>
/// A module whose PDB is not on a server costs TraceEvent twelve or more requests (per server: the plain and the XX/ index path,
/// each as .pdb, .pd_ and file.ptr), one after the other, each waited for up to its ServerTimeout of 60 s. A server that hangs on
/// some of them - symbols.nuget.org did, 2026-11-11, the day after a Windows update whose PDBs were not all published - then costs
/// 10 to 160 s a module: 618 s of "Loading Modules" for a trace that took 3 s the day before.
/// </para>
/// <para>
/// Here a request gets <see cref="Timeout"/> to answer with its response headers (a download that has started is never cut). The
/// first request of a server that runs out of it, or fails before any answer (connection refused or reset, no name, TLS), takes
/// the server out: its next requests are answered at once with 503, without the network, until <see cref="Resume"/>. So a server
/// that is down costs one timeout a pass, not one or more a module.
/// </para>
/// <para>
/// Around each module's lookup <see cref="BeginLookup"/> and <see cref="EndLookup"/> say how it went: a module that got no PDB while
/// a server was out, timed out or answered with a server error (<see cref="Outcome.Incomplete"/>) has not been told "no" by every
/// server, and is worth the one retry at the end; a clean 404 or 403 from all of them (<see cref="Outcome.NotFound"/>) is final.
/// </para>
/// </summary>
internal sealed class UltraSymbolServerGuard : DelegatingHandler
{
    /// <summary>How a module's symbol lookup went, by the requests it made (<see cref="EndLookup"/>).</summary>
    public enum Outcome
    {
        /// <summary>No request: the PDB was beside the module or in the cache, or the module has none to ask for.</summary>
        NoRequest,
        /// <summary>A server answered a request with success: a file came from it.</summary>
        Found,
        /// <summary>Every request was answered, none with success: the servers do not have it.</summary>
        NotFound,
        /// <summary>No success, and not every server said no: one was out, timed out, failed or answered with a server error.</summary>
        Incomplete,
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _out = new(StringComparer.OrdinalIgnoreCase);   // the servers taken out, and why
    private readonly Action<string>? _serverOut;   // hears each server taken out: "host: why"
    private int _requests;      // of the lookup under way
    private int _successes;
    private bool _incomplete;

    /// <param name="timeout">what a request gets to answer with its response headers; zero or less: no limit of its own</param>
    /// <param name="innerHandler">the handler that sends (tests); default the one HttpClient uses</param>
    /// <param name="serverOut">hears a server taken out: its host and the reason, one line</param>
    public UltraSymbolServerGuard(TimeSpan timeout, HttpMessageHandler? innerHandler = null, Action<string>? serverOut = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        Timeout = timeout;
        _serverOut = serverOut;
    }

    /// <summary>What a request gets to answer with its response headers.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The servers taken out since the last <see cref="Resume"/>.</summary>
    public IReadOnlyList<string> ServersOut
    {
        get { lock (_gate) return _out.Keys.ToArray(); }
    }

    /// <summary>The requests sent or refused since the guard was made (tests, the summary).</summary>
    public int TotalRequests { get; private set; }

    /// <summary>A module's lookup starts: its requests are counted from here.</summary>
    public void BeginLookup()
    {
        lock (_gate)
        {
            _requests = 0;
            _successes = 0;
            _incomplete = false;
        }
    }

    /// <summary>The lookup begun with <see cref="BeginLookup"/> is over: how it went.</summary>
    public Outcome EndLookup()
    {
        lock (_gate)
        {
            if (_requests == 0) return Outcome.NoRequest;
            if (_successes > 0) return Outcome.Found;
            return _incomplete ? Outcome.Incomplete : Outcome.NotFound;
        }
    }

    /// <summary>The servers taken out are asked again (the retry pass): the first that fails again is out again.</summary>
    public void Resume()
    {
        lock (_gate) _out.Clear();
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var server = request.RequestUri?.Authority ?? string.Empty;
        lock (_gate)
        {
            _requests++;
            TotalRequests++;
            if (_out.ContainsKey(server))
            {
                _incomplete = true;
                // answered here, without the network: TraceEvent takes any non-success status as "not on this server"
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    ReasonPhrase = "Symbol server skipped: it did not answer an earlier request",
                };
            }
        }

        HttpResponseMessage response;
        using var sendSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var send = base.SendAsync(request, sendSource.Token);
        try
        {
            if (Timeout > TimeSpan.Zero && !send.IsCompleted)
            {
                // the clock covers the name, the connection, TLS and the response headers; once the headers are here the request
                // is alive and its body is the caller's to read, however long it takes (a PDB of 80 MB)
                using var clockSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var first = await Task.WhenAny(send, Task.Delay(Timeout, clockSource.Token)).ConfigureAwait(false);
                clockSource.Cancel();
                if (first != send)
                {
                    cancellationToken.ThrowIfCancellationRequested();   // the caller gave up, not the server
                    sendSource.Cancel();
                    // the abandoned request ends on its own: its error is nobody's, an answer that still comes is closed
                    _ = send.ContinueWith(static t =>
                    {
                        if (t.IsFaulted) _ = t.Exception;
                        else if (t.IsCompletedSuccessfully) t.Result.Dispose();
                    }, TaskScheduler.Default);
                    var why = string.Create(CultureInfo.InvariantCulture, $"no answer in {Timeout.TotalSeconds:0.#}s");
                    TakeOut(server, why);
                    throw new TimeoutException($"Symbol server {server}: {why} ({request.RequestUri})");
                }
            }

            response = await send.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // the caller's cancellation (HttpClient's own timeout included) says nothing about the server
        }
        catch (TimeoutException)
        {
            throw;   // ours, the server is out already
        }
        catch (Exception e)
        {
            // no answer at all: refused, reset, no such host, TLS
            TakeOut(server, Describe(e));
            throw;
        }

        var status = (int)response.StatusCode;
        lock (_gate)
        {
            if (response.IsSuccessStatusCode) _successes++;
            // the server answered, but not with a yes or a no about this file: asked once more at the end, the server stays in
            else if (status == 408 || status == 429 || status >= 500) _incomplete = true;
        }

        return response;
    }

    private void TakeOut(string server, string why)
    {
        lock (_gate)
        {
            _incomplete = true;
            if (!_out.TryAdd(server, why)) return;
        }

        _serverOut?.Invoke($"{server}: {why}");
    }

    /// <summary>The innermost reason of <paramref name="e"/> on one line.</summary>
    private static string Describe(Exception e)
    {
        while (e.InnerException != null) e = e.InnerException;
        var text = e.Message.ReplaceLineEndings(" ").Trim();
        if (text.Length > 160) text = text[..160] + "...";
        return text.Length == 0 ? e.GetType().Name : text;
    }
}

/// <summary>What <see cref="UltraSymbolLookup.Run{T}"/> did.</summary>
/// <param name="Modules">the modules looked up</param>
/// <param name="NotFound">the names of those the servers were asked for and that got no PDB, the retry included</param>
/// <param name="Retried">the modules looked up a second time</param>
/// <param name="FoundOnRetry">of those, the ones that got their PDB then</param>
/// <param name="Seconds">the time of it all</param>
internal readonly record struct UltraSymbolLookupSummary(int Modules, IReadOnlyList<string> NotFound, int Retried, int FoundOnRetry, double Seconds)
{
    /// <summary>One line for the log, the same text whatever the culture of the machine (0.0s, not 0,0s).</summary>
    public override string ToString()
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"Symbols of {Modules} module{(Modules == 1 ? "" : "s")} looked up in {Seconds:0.0}s");
        if (Retried > 0) text += $"; {Retried} a second time after a symbol server did not answer, {FoundOnRetry} found then";
        if (NotFound.Count > 0)
        {
            const int listed = 12;
            text += $"; {NotFound.Count} not on the symbol servers (their frames stay module!0xADDRESS): {string.Join(", ", NotFound.Take(listed))}";
            if (NotFound.Count > listed) text += $" and {NotFound.Count - listed} more";
        }

        return text;
    }
}

/// <summary>
/// The symbol lookup of a conversion's modules, with the ONE retry of <see cref="UltraSymbolServerGuard"/>: every module once, then
/// once more those whose lookup was left <see cref="UltraSymbolServerGuard.Outcome.Incomplete"/> - with the servers that were
/// taken out asked again, each until it fails again.
/// </summary>
internal static class UltraSymbolLookup
{
    /// <summary>A lookup that took this long is a line in the log, with how it ended.</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromSeconds(2);

    /// <param name="modules">the modules to look up, in order</param>
    /// <param name="nameOf">a module's name for the messages</param>
    /// <param name="lookup">looks one module up (TraceLog.CodeAddresses.LookupSymbolsForModule)</param>
    /// <param name="guard">the HTTP handler the lookups go through; null: no retry, nothing known about how a lookup went</param>
    /// <param name="forgetFailures">called once before the retry: TraceEvent's SymbolReader remembers a PDB it did not find and
    /// answers the second lookup from that memory unless told to forget</param>
    /// <param name="progress">messages kept in the log</param>
    /// <param name="step">the module under way, replaced by the next</param>
    public static UltraSymbolLookupSummary Run<T>(IReadOnlyList<T> modules, Func<T, string> nameOf, Action<T> lookup, UltraSymbolServerGuard? guard,
        Action? forgetFailures = null, Action<string>? progress = null, Action<string>? step = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var notFound = new List<string>();
        List<T>? again = null;
        for (var i = 0; i < modules.Count; i++)
        {
            var module = modules[i];
            step?.Invoke($"Loading Symbols [{i + 1}/{modules.Count}] for Module `{nameOf(module)}`");
            var outcome = One(module);
            if (outcome == UltraSymbolServerGuard.Outcome.Incomplete) (again ??= new()).Add(module);
            else if (outcome == UltraSymbolServerGuard.Outcome.NotFound) notFound.Add(nameOf(module));
        }

        var foundOnRetry = 0;
        if (again != null && guard != null)
        {
            progress?.Invoke($"Retrying the symbols of {again.Count} module{(again.Count == 1 ? "" : "s")} once: a symbol server did not answer ({string.Join(", ", guard.ServersOut)})");
            guard.Resume();
            forgetFailures?.Invoke();
            for (var i = 0; i < again.Count; i++)
            {
                var module = again[i];
                step?.Invoke($"Loading Symbols, retry [{i + 1}/{again.Count}] for Module `{nameOf(module)}`");
                if (One(module) == UltraSymbolServerGuard.Outcome.Found) foundOnRetry++;
                else notFound.Add(nameOf(module));
            }
        }

        return new UltraSymbolLookupSummary(modules.Count, notFound, again?.Count ?? 0, foundOnRetry, clock.Elapsed.TotalSeconds);

        UltraSymbolServerGuard.Outcome One(T module)
        {
            guard?.BeginLookup();
            var time = System.Diagnostics.Stopwatch.StartNew();
            lookup(module);
            var outcome = guard?.EndLookup() ?? UltraSymbolServerGuard.Outcome.NoRequest;
            if (time.Elapsed >= Slow)
            {
                var how = outcome switch
                {
                    UltraSymbolServerGuard.Outcome.Found => "downloaded",
                    UltraSymbolServerGuard.Outcome.NotFound => "not on the symbol servers",
                    UltraSymbolServerGuard.Outcome.Incomplete => "not found, a symbol server did not answer",
                    _ => "no symbol server asked",
                };
                progress?.Invoke(string.Create(CultureInfo.InvariantCulture, $"Symbols of module `{nameOf(module)}` took {time.Elapsed.TotalSeconds:0.0}s: {how}"));
            }

            return outcome;
        }
    }
}
