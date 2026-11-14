// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Diagnostics.Symbols;
using Ultra.Core;

namespace Ultra.Tests;

/// <summary>
/// The symbol servers of these tests are a handler under the guard: no request leaves the process.
/// </summary>
[TestClass]
public class UltraSymbolServerGuardTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);

    /// <summary>A server by host: what it answers, and the requests that reached it.</summary>
    private sealed class FakeServers : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> Hosts = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Reached = [];
        public CancellationToken LastToken;

        public int ReachedOf(string host)
        {
            lock (Reached) return Reached.Count(x => x.Equals(host, StringComparison.OrdinalIgnoreCase));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            lock (Reached) Reached.Add(host);
            LastToken = cancellationToken;
            return Hosts[host](request, cancellationToken);
        }

        public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Status(HttpStatusCode status)
            => (request, _) => Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request, Content = new ByteArrayContent([1, 2, 3]) });

        /// <summary>Never answers: ends only when the request is cancelled.</summary>
        public static async Task<HttpResponseMessage> Hang(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }

    private static Task<HttpResponseMessage> Get(HttpMessageInvoker client, string url, CancellationToken cancellationToken = default)
        => client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

    [TestMethod]
    public async Task AServerThatDoesNotAnswerCostsOneTimeoutThenIsSkipped()
    {
        var servers = new FakeServers();
        servers.Hosts["slow.test"] = FakeServers.Hang;
        servers.Hosts["ok.test"] = FakeServers.Status(HttpStatusCode.NotFound);
        var taken = new List<string>();
        var guard = new UltraSymbolServerGuard(Short, servers, taken.Add);
        using var client = new HttpMessageInvoker(guard);

        guard.BeginLookup();
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => Get(client, "http://slow.test/a.pdb/1/a.pdb"));
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(5), $"the timeout took {clock.Elapsed}");

        // the server is out: its next requests are answered here, at once, and never reach it
        clock.Restart();
        for (var i = 0; i < 5; i++)
        {
            using var skipped = await Get(client, "http://slow.test/a.pdb/1/a.pd_");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, skipped.StatusCode);
        }

        Assert.IsTrue(clock.Elapsed < Short, $"five skipped requests took {clock.Elapsed}");
        Assert.AreEqual(1, servers.ReachedOf("slow.test"));

        // another server is not touched by it
        using var other = await Get(client, "http://ok.test/a.pdb/1/a.pdb");
        Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);

        Assert.AreEqual(UltraSymbolServerGuard.Outcome.Incomplete, guard.EndLookup());
        CollectionAssert.AreEqual(new[] { "slow.test" }, guard.ServersOut.ToArray());
        Assert.AreEqual(1, taken.Count, "one message a server taken out");
        StringAssert.StartsWith(taken[0], "slow.test: no answer in ");
        Assert.AreEqual(7, guard.TotalRequests);
    }

    [TestMethod]
    public async Task ACleanNoFromEveryServerIsFinal()
    {
        var servers = new FakeServers();
        servers.Hosts["a.test"] = FakeServers.Status(HttpStatusCode.NotFound);
        servers.Hosts["b.test"] = FakeServers.Status(HttpStatusCode.Forbidden);
        var guard = new UltraSymbolServerGuard(Short, servers);
        using var client = new HttpMessageInvoker(guard);

        guard.BeginLookup();
        (await Get(client, "http://a.test/x")).Dispose();
        (await Get(client, "http://b.test/x")).Dispose();
        Assert.AreEqual(UltraSymbolServerGuard.Outcome.NotFound, guard.EndLookup());
        Assert.AreEqual(0, guard.ServersOut.Count);

        // a lookup without a request (the PDB beside the module or in the cache) is none of the above
        guard.BeginLookup();
        Assert.AreEqual(UltraSymbolServerGuard.Outcome.NoRequest, guard.EndLookup());
    }

    [TestMethod]
    public async Task ASuccessIsFoundWhateverElseHappened()
    {
        var servers = new FakeServers();
        servers.Hosts["slow.test"] = FakeServers.Hang;
        servers.Hosts["has.test"] = FakeServers.Status(HttpStatusCode.OK);
        var guard = new UltraSymbolServerGuard(Short, servers);
        using var client = new HttpMessageInvoker(guard);

        guard.BeginLookup();
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => Get(client, "http://slow.test/x"));
        (await Get(client, "http://has.test/x")).Dispose();
        Assert.AreEqual(UltraSymbolServerGuard.Outcome.Found, guard.EndLookup());
    }

    [TestMethod]
    public async Task AServerErrorIsAskedAgainLaterButTheServerStays()
    {
        var servers = new FakeServers();
        servers.Hosts["busy.test"] = FakeServers.Status(HttpStatusCode.ServiceUnavailable);
        var guard = new UltraSymbolServerGuard(Short, servers);
        using var client = new HttpMessageInvoker(guard);

        guard.BeginLookup();
        (await Get(client, "http://busy.test/x")).Dispose();
        (await Get(client, "http://busy.test/y")).Dispose();
        Assert.AreEqual(UltraSymbolServerGuard.Outcome.Incomplete, guard.EndLookup());
        Assert.AreEqual(0, guard.ServersOut.Count);
        Assert.AreEqual(2, servers.ReachedOf("busy.test"), "a server that answers is not skipped");
    }

    [TestMethod]
    public async Task AConnectionThatFailsTakesTheServerOutAndResumeAsksItAgain()
    {
        var servers = new FakeServers();
        var fail = true;
        servers.Hosts["reset.test"] = (request, _) => fail
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("An error occurred while sending the request.", new IOException("forcibly closed")))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
        var taken = new List<string>();
        var guard = new UltraSymbolServerGuard(Short, servers, taken.Add);
        using var client = new HttpMessageInvoker(guard);

        guard.BeginLookup();
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => Get(client, "http://reset.test/x"));
        using (var skipped = await Get(client, "http://reset.test/y")) Assert.AreEqual(HttpStatusCode.ServiceUnavailable, skipped.StatusCode);
        Assert.AreEqual(UltraSymbolServerGuard.Outcome.Incomplete, guard.EndLookup());
        Assert.AreEqual("reset.test: forcibly closed", taken.Single());   // the innermost reason

        fail = false;
        guard.Resume();
        Assert.AreEqual(0, guard.ServersOut.Count);
        guard.BeginLookup();
        using (var answered = await Get(client, "http://reset.test/x")) Assert.AreEqual(HttpStatusCode.OK, answered.StatusCode);
        Assert.AreEqual(UltraSymbolServerGuard.Outcome.Found, guard.EndLookup());
        Assert.AreEqual(2, servers.ReachedOf("reset.test"));
    }

    /// <summary>The timeout is for the response headers: a download that has started is the caller's, however long it takes.</summary>
    [TestMethod]
    public async Task ADownloadThatHasStartedIsNotCut()
    {
        var servers = new FakeServers();
        // the headers come inside the timeout, on the clock's path
        servers.Hosts["has.test"] = async (request, token) =>
        {
            await Task.Delay(Short / 5, token);
            return await FakeServers.Status(HttpStatusCode.OK)(request, token);
        };
        var guard = new UltraSymbolServerGuard(Short, servers);
        using var client = new HttpMessageInvoker(guard);

        using var response = await Get(client, "http://has.test/big.pdb");
        await Task.Delay(Short * 3);
        Assert.IsFalse(servers.LastToken.IsCancellationRequested, "the request was cancelled after its headers came");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(0, guard.ServersOut.Count);
    }

    [TestMethod]
    public async Task TheCallersCancellationSaysNothingAboutTheServer()
    {
        var servers = new FakeServers();
        servers.Hosts["slow.test"] = FakeServers.Hang;
        var guard = new UltraSymbolServerGuard(TimeSpan.FromSeconds(30), servers);
        using var client = new HttpMessageInvoker(guard);
        using var cancel = new CancellationTokenSource(Short);

        await Assert.ThrowsAsync<OperationCanceledException>(() => Get(client, "http://slow.test/x", cancel.Token));
        Assert.AreEqual(0, guard.ServersOut.Count);
    }

    /// <summary>
    /// Every module once; then once more, and only once, those a server did not answer for - with that server asked again.
    /// </summary>
    [TestMethod]
    public void TheModulesAServerDidNotAnswerForAreLookedUpOnceMore()
    {
        var servers = new FakeServers();
        var slowAnswers = false;
        servers.Hosts["ms.test"] = FakeServers.Status(HttpStatusCode.NotFound);
        servers.Hosts["slow.test"] = (request, token) => !slowAnswers
            ? FakeServers.Hang(request, token)
            // on the retry it has the PDB of "late", and a clean no for the others
            : FakeServers.Status(request.RequestUri!.AbsolutePath.Contains("late") ? HttpStatusCode.OK : HttpStatusCode.NotFound)(request, token);
        var guard = new UltraSymbolServerGuard(Short, servers);
        using var client = new HttpMessageInvoker(guard);

        var lookups = new List<string>();
        var forgotten = 0;
        var progress = new List<string>();
        var steps = new List<string>();
        void Lookup(string module)
        {
            lookups.Add(module);
            if (module == "local") return;   // its PDB is in the cache: no request
            foreach (var server in new[] { "ms.test", "slow.test" })
            {
                try
                {
                    // blocking, as TraceEvent's reader does it
                    using var response = Get(client, $"http://{server}/{module}.pdb").GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode) return;
                }
                catch (TimeoutException)
                {
                    // TraceEvent logs the failure of a request and tries the next
                }
            }
        }

        var summary = UltraSymbolLookup.Run(["local", "late", "never", "gone"], x => x, Lookup, guard,
            forgetFailures: () => { forgotten++; slowAnswers = true; },
            progress: progress.Add, step: steps.Add);

        // "late" and "never" hit the server that hangs ("late" pays the one timeout, "never" and "gone" find it out);
        // all three were not told no by every server, so all three are asked again - once
        CollectionAssert.AreEqual(new[] { "local", "late", "never", "gone", "gone", "never", "late" }, lookups);
        Assert.AreEqual(1, forgotten);
        Assert.AreEqual(4, summary.Modules);
        Assert.AreEqual(3, summary.Retried);
        Assert.AreEqual(1, summary.FoundOnRetry);
        CollectionAssert.AreEquivalent(new[] { "never", "gone" }, summary.NotFound.ToArray());
        Assert.AreEqual(4, servers.ReachedOf("slow.test"), "the first pass reached the hanging server once, the retry three times");
        Assert.AreEqual("Retrying the symbols of 3 modules once: a symbol server did not answer (slow.test)", progress.Single(x => x.StartsWith("Retrying")));
        CollectionAssert.Contains(steps, "Loading Symbols [1/4] for Module `local`");
        CollectionAssert.Contains(steps, "Loading Symbols, retry [3/3] for Module `late`");
        StringAssert.Contains(summary.ToString(), "Symbols of 4 modules looked up in ");
        StringAssert.Contains(summary.ToString(), "; 3 a second time after a symbol server did not answer, 1 found then; 2 not on the symbol servers (their frames stay module!0xADDRESS): gone, never");
    }

    [TestMethod]
    public void ASlowPdbDoesNotBlockTheOtherModulesOnRetry()
    {
        var servers = new FakeServers();
        servers.Hosts["symbols.test"] = (request, token) => request.RequestUri!.AbsolutePath.Contains("slow")
            ? FakeServers.Hang(request, token)
            : FakeServers.Status(HttpStatusCode.OK)(request, token);
        var guard = new UltraSymbolServerGuard(Short, servers);
        using var client = new HttpMessageInvoker(guard);
        var found = new List<string>();
        void Lookup(string module)
        {
            try
            {
                using var response = Get(client, $"http://symbols.test/{module}.pdb").GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode) found.Add(module);
            }
            catch (TimeoutException) { }
        }

        var summary = UltraSymbolLookup.Run(["slow", "good1", "good2"], x => x, Lookup, guard);

        CollectionAssert.AreEquivalent(new[] { "good1", "good2" }, found);
        Assert.AreEqual(2, summary.FoundOnRetry);
        Assert.AreEqual(0, summary.NotFound.Count, "a timeout does not prove the PDB is absent");
        StringAssert.Contains(summary.ToString(), "unresolved because a symbol server did not answer");
    }

    [TestMethod]
    public void WithoutAProblemThereIsNoRetry()
    {
        var servers = new FakeServers();
        servers.Hosts["ms.test"] = FakeServers.Status(HttpStatusCode.NotFound);
        var guard = new UltraSymbolServerGuard(Short, servers);
        using var client = new HttpMessageInvoker(guard);
        var lookups = 0;

        var summary = UltraSymbolLookup.Run(["a", "b"], x => x, module =>
        {
            lookups++;
            Get(client, $"http://ms.test/{module}.pdb").GetAwaiter().GetResult().Dispose();
        }, guard, forgetFailures: () => Assert.Fail("nothing to forget"));

        Assert.AreEqual(2, lookups);
        Assert.AreEqual(0, summary.Retried);
        CollectionAssert.AreEqual(new[] { "a", "b" }, summary.NotFound.ToArray());

        // and without a guard (--symbol-timeout 0) the lookups run as they are, nothing known about them
        lookups = 0;
        summary = UltraSymbolLookup.Run(["a", "b"], x => x, _ => lookups++, guard: null);
        Assert.AreEqual(2, lookups);
        Assert.AreEqual(0, summary.NotFound.Count);
        Assert.AreEqual("Symbols of 2 modules looked up in 0.0s", summary.ToString());
    }

    /// <summary>The summary is a line of the log: the same text on a machine whose culture writes 1,5 (the test above failed on one).</summary>
    [TestMethod]
    public void TheSummaryReadsTheSameInAnyCulture()
    {
        var before = CultureInfo.CurrentCulture;
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        try
        {
            CultureInfo.CurrentCulture = comma;
            Assert.AreEqual("1,5", 1.5.ToString("0.0"), "the culture of this test writes a decimal comma");
            Assert.AreEqual("Symbols of 2 modules looked up in 1.5s", new UltraSymbolLookupSummary(2, [], 0, 0, 1.5).ToString());
            Assert.AreEqual("Symbols of 1 module looked up in 12.3s; 1 a second time after a symbol server did not answer, 0 found then; 1 not on the symbol servers (their frames stay module!0xADDRESS): ntdll",
                new UltraSymbolLookupSummary(1, ["ntdll"], 1, 0, 12.34).ToString());

            // and the lookup's own summary, as the test above reads it
            Assert.AreEqual("Symbols of 2 modules looked up in 0.0s", UltraSymbolLookup.Run(["a", "b"], x => x, _ => { }, guard: null).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>
    /// The retry relies on TraceEvent's SymbolReader: it takes our handler, it remembers a PDB it did not find (so a second lookup
    /// sends nothing), and setting its SymbolPath makes it forget. If an update of TraceEvent changes that, the retry silently
    /// does nothing.
    /// </summary>
    [TestMethod]
    public void TheReaderAsksAgainOnlyAfterItsPathWasSet()
    {
        var servers = new FakeServers();
        servers.Hosts["symbols.test"] = FakeServers.Status(HttpStatusCode.NotFound);
        var guard = new UltraSymbolServerGuard(TimeSpan.FromSeconds(10), servers);
        var cache = Path.Combine(Path.GetTempPath(), "ultra-guard-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var reader = new SymbolReader(TextWriter.Null, $"SRV*{cache}*http://symbols.test/download", guard);
            reader.SecurityCheck = _ => true;
            var id = Guid.NewGuid();

            guard.BeginLookup();
            Assert.IsNull(reader.FindSymbolFilePath("nowhere.pdb", id, 1));
            Assert.AreEqual(UltraSymbolServerGuard.Outcome.NotFound, guard.EndLookup());
            var first = servers.ReachedOf("symbols.test");
            Assert.IsTrue(first >= 3, $"{first} requests for a PDB that is not there (.pdb, .pd_, file.ptr)");

            guard.BeginLookup();
            Assert.IsNull(reader.FindSymbolFilePath("nowhere.pdb", id, 1));
            Assert.AreEqual(UltraSymbolServerGuard.Outcome.NoRequest, guard.EndLookup(), "the reader no longer remembers a failed lookup: the retry needs no forgetting");

            reader.SymbolPath = reader.SymbolPath;
            guard.BeginLookup();
            Assert.IsNull(reader.FindSymbolFilePath("nowhere.pdb", id, 1));
            Assert.AreEqual(UltraSymbolServerGuard.Outcome.NotFound, guard.EndLookup(), "setting the reader's path no longer makes it ask again: the retry does nothing");
            Assert.AreEqual(2 * first, servers.ReachedOf("symbols.test"));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }
}
