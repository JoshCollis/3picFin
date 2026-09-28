using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Discovery;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Discovery;

public sealed class SeerrReadCacheTests
{
    private static readonly Guid Alice = Guid.Parse("f6b48a38-9e4b-4b1b-a957-e8e425e91922");
    private static readonly Guid Bob = Guid.Parse("e1bc172d-6870-4bad-8bc7-32af478474bc");
    private static SeerrOptions Options() => SeerrOptions.FromConfiguration(new PluginConfiguration
        { SeerrEnabled = true, SeerrBaseUrl = "https://seerr.example/seerr", SeerrApiKey = "test-secret" })!;
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => run(request, token);
    }

    [Fact]
    public async Task ConcurrentDiscoveryCoalescesSourcesButRevalidatesMappingPerCaller()
    {
        var paths = new ConcurrentBag<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            paths.Add(path);
            if (path.Contains("/user/jellyfin/", StringComparison.Ordinal)) return Json("{\"id\":42}");
            await Task.Delay(40, token);
            return Json("{\"results\":[]}");
        }));
        var client = new SeerrClient(http, Options(), readCache: new SeerrReadCache());
        var tasks = Enumerable.Range(0, 20).Select(_ => client.GetDiscoveryAsync(Alice, 1, 20, CancellationToken.None)).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.True(result.Movies.IsSuccess && result.Tv.IsSuccess && result.Requests.IsSuccess));
        Assert.Equal(20, paths.Count(path => path.Contains("/user/jellyfin/", StringComparison.Ordinal)));
        Assert.Equal(1, paths.Count(path => path.Contains("/movies", StringComparison.Ordinal)));
        Assert.Equal(1, paths.Count(path => path.Contains("/tv", StringComparison.Ordinal)));
        Assert.Equal(1, paths.Count(path => path.Contains("/request?", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DifferentUsersPagesAndQueriesNeverShareAndMappingChangeFailsClosed()
    {
        var reads = new ConcurrentBag<string>();
        var mapped = 42;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("/user/jellyfin/", StringComparison.Ordinal))
                return Task.FromResult(Json($"{{\"id\":{mapped}}}"));
            reads.Add(path + ":" + string.Join(",", request.Headers.GetValues("X-API-User")));
            return Task.FromResult(Json("{\"results\":[]}"));
        }));
        var client = new SeerrClient(http, Options(), readCache: new SeerrReadCache());
        await client.GetUserReadAsync(Alice, "api/v1/search?query=one&page=1", CancellationToken.None);
        await client.GetUserReadAsync(Alice, "api/v1/search?query=one&page=1", CancellationToken.None);
        await client.GetUserReadAsync(Bob, "api/v1/search?query=one&page=1", CancellationToken.None);
        await client.GetUserReadAsync(Alice, "api/v1/search?query=two&page=1", CancellationToken.None);
        await client.GetUserReadAsync(Alice, "api/v1/search?query=one&page=2", CancellationToken.None);
        mapped = 57;
        await client.GetUserReadAsync(Alice, "api/v1/search?query=one&page=1", CancellationToken.None);
        Assert.Equal(5, reads.Count);
        Assert.Contains(reads, r => r.EndsWith(":57", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PermissionChangeWithSameMappedIdForcesFreshRead()
    {
        var permission = 32;
        var reads = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/", StringComparison.Ordinal))
                return Task.FromResult(Json($"{{\"id\":42,\"permissions\":{permission}}}"));
            Interlocked.Increment(ref reads);
            return Task.FromResult(Json("{\"results\":[]}"));
        }));
        var client = new SeerrClient(http, Options(), readCache: new SeerrReadCache());
        await client.GetUserReadAsync(Alice, "api/v1/search?query=a", CancellationToken.None);
        permission = 0;
        await client.GetUserReadAsync(Alice, "api/v1/search?query=a", CancellationToken.None);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelSharedRead()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/", StringComparison.Ordinal)) return Json("{\"id\":42}");
            Interlocked.Increment(ref reads);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Json("{\"results\":[]}");
        }));
        var client = new SeerrClient(http, Options(), readCache: new SeerrReadCache());
        using var cancel = new CancellationTokenSource();
        var first = client.GetUserReadAsync(Alice, "api/v1/search?query=a&page=1", cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = client.GetUserReadAsync(Alice, "api/v1/search?query=a&page=1", CancellationToken.None);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        Assert.True((await second).IsSuccess);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task FailuresAreNotCachedAndPersonalReadsExpireOrInvalidateOnWrite()
    {
        var reads = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("/user/jellyfin/", StringComparison.Ordinal)) return Task.FromResult(Json("{\"id\":42,\"permissions\":32}"));
            if (request.Method == HttpMethod.Post) return Task.FromResult(Json("{\"id\":7,\"status\":2}", HttpStatusCode.Created));
            if (path.Contains("/movie/1", StringComparison.Ordinal)) return Task.FromResult(Json("{\"id\":1}"));
            var count = Interlocked.Increment(ref reads);
            return Task.FromResult(Json("{\"results\":[]}", count == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK));
        }));
        var client = new SeerrClient(http, Options(), readCache: new SeerrReadCache(TimeSpan.FromMilliseconds(60)));
        Assert.False((await client.GetPersonalRequestsAsync(Alice, 20, 0, CancellationToken.None)).IsSuccess);
        Assert.True((await client.GetPersonalRequestsAsync(Alice, 20, 0, CancellationToken.None)).IsSuccess);
        await client.GetPersonalRequestsAsync(Alice, 20, 0, CancellationToken.None);
        Assert.Equal(2, reads);
        await Task.Delay(90);
        await client.GetPersonalRequestsAsync(Alice, 20, 0, CancellationToken.None);
        Assert.Equal(3, reads);
        Assert.Null((await client.CreateRequestAsync(Alice, new CreateSeerrRequest("movie", 1, null, false), CancellationToken.None)).Failure);
        await client.GetPersonalRequestsAsync(Alice, 20, 0, CancellationToken.None);
        Assert.Equal(4, reads);
    }

    [Fact]
    public async Task StubBenchmarkReportsRequestCountAndP95()
    {
        var upstream = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Interlocked.Increment(ref upstream);
            if (request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/", StringComparison.Ordinal)) return Json("{\"id\":42}");
            await Task.Delay(25, token);
            return Json("{\"results\":[]}");
        }));
        async Task<double[]> Measure(bool shared)
        {
            var cache = new SeerrReadCache();
            var samples = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            {
                var client = new SeerrClient(http, Options(), readCache: shared ? cache : new SeerrReadCache());
                var watch = Stopwatch.StartNew();
                await client.GetDiscoveryAsync(Alice, 1, 20, CancellationToken.None);
                return watch.Elapsed.TotalMilliseconds;
            }));
            Array.Sort(samples);
            return samples;
        }
        var baseline = await Measure(false);
        Assert.Equal(80, upstream);
        upstream = 0;
        var samples = await Measure(true);
        // Deterministic upstream count; latency is reported, not asserted against a noisy CI threshold.
        Assert.Equal(23, upstream);
        Console.WriteLine($"stub discovery, 20 concurrent callers: baseline requests=80 p95_ms={baseline[18]:F2}; coalesced requests={upstream} p95_ms={samples[18]:F2}");
    }

    [Fact]
    public async Task MappingAndReadShareOneOperationDeadline()
    {
        var reads = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/", StringComparison.Ordinal))
            {
                await Task.Delay(220, token);
                return Json("{\"id\":42}");
            }
            Interlocked.Increment(ref reads);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        }));
        var client = new SeerrClient(http, Options(), TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        var result = await client.GetUserReadAsync(Alice, "api/v1/search?query=deadline", CancellationToken.None);
        Assert.Equal(SeerrFailure.UpstreamUnavailable, result.Failure);
        Assert.Equal(1, reads);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(440), $"elapsed={watch.Elapsed}");
    }

    [Fact]
    public async Task ExpiredMappedReadReleasesAdmissionForUnrelatedReads()
    {
        var cache = new SeerrReadCache();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/", StringComparison.Ordinal))
            {
                await Task.Delay(150, token);
                return Json("{\"id\":42}");
            }
            readStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { readCancelled.TrySetResult(); throw; }
            return Json("{}");
        }));
        var client = new SeerrClient(http, Options(), TimeSpan.FromMilliseconds(300), cache);
        var pending = client.GetUserReadAsync(Alice, "api/v1/search?query=expired", CancellationToken.None);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SeerrFailure.UpstreamUnavailable, (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Failure);
        await readCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstBatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        async Task<SeerrResult> Load(CancellationToken token)
        {
            var count = Interlocked.Increment(ref started);
            if (count == 31) firstBatchStarted.TrySetResult();
            if (count == 32) allStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return SeerrResult.Success(System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone());
        }
        // Fill 31 slots first. A successful 32nd admission is observable proof
        // that the expired read has released its slot (the handler's cancellation
        // signal alone fires before LoadAsync's semaphore finally block).
        var calls = Enumerable.Range(0, 31).Select(i => cache.GetAsync("other", Bob, 57,
            $"api/v1/search?query={i}", Load, TimeSpan.FromSeconds(10), CancellationToken.None)).ToList();
        try
        {
            await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(calls, call => Assert.False(call.IsCompleted));
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var last = cache.GetAsync("other", Bob, 57, "api/v1/search?query=31",
                    Load, TimeSpan.FromSeconds(10), CancellationToken.None);
                if (!last.IsCompleted)
                {
                    calls.Add(last);
                    break;
                }
                Assert.Equal(SeerrFailure.UpstreamUnavailable, (await last).Failure);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "Expired read did not release its upstream slot");
                await Task.Yield();
            }
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(32, started);
            Assert.All(calls, call => Assert.False(call.IsCompleted));
        }
        finally { release.TrySetResult(); }
        Assert.All(await Task.WhenAll(calls), result => Assert.True(result.IsSuccess));
    }

    [Fact]
    public async Task ShortDeadlineDoesNotCancelLongerCoalescedWaiter()
    {
        var cache = new SeerrReadCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        async Task<SeerrResult> Load(CancellationToken token)
        {
            Interlocked.Increment(ref reads);
            started.TrySetResult();
            try { await release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return SeerrResult.Success(System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone());
        }
        using var shortDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        var first = cache.GetAsync("scope", Alice, 42, "api/v1/search?query=shared", Load,
            TimeSpan.FromMilliseconds(120), shortDeadline.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = cache.GetAsync("scope", Alice, 42, "api/v1/search?query=shared", Load,
            TimeSpan.FromSeconds(1), CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(cancelled.Task.IsCompleted);
        release.SetResult();
        Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task LastCancelledWaiterStopsSharedReadAndAllowsFreshRead()
    {
        var cache = new SeerrReadCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        async Task<SeerrResult> Load(CancellationToken token)
        {
            Interlocked.Increment(ref count);
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled.TrySetResult(); throw; }
            return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable);
        }
        using var stop = new CancellationTokenSource();
        var first = cache.GetAsync("scope", Alice, 42, "api/v1/search?query=cancel", Load,
            TimeSpan.FromSeconds(2), stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await cancelled.Task.WaitAsync(TimeSpan.FromMilliseconds(200));
        var next = await cache.GetAsync("scope", Alice, 42, "api/v1/search?query=cancel",
            _ => Task.FromResult(SeerrResult.Success(System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone())),
            TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(next.IsSuccess);
        Assert.Equal(1, count);
    }

    private static async Task SpinWaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout) await Task.Delay(5);
    }

    [Fact]
    public async Task MappingBurstHasBoundedAdmissionAndDoesNotQueue()
    {
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref started);
                await release.Task.WaitAsync(token);
                return Json("{\"id\":42}");
            }
            return Json("{}");
        }));
        var client = new SeerrClient(http, Options(), TimeSpan.FromSeconds(2));
        var calls = Enumerable.Range(0, 100).Select(_ => client.GetUserReadAsync(Alice, "api/v1/search?query=burst", CancellationToken.None)).ToArray();
        try
        {
            var completed = await Task.WhenAll(calls.Where(t => t.IsCompleted)).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.NotEmpty(completed);
            Assert.All(completed, r => Assert.Equal(SeerrFailure.UpstreamUnavailable, r.Failure));
            Assert.InRange(Volatile.Read(ref started), 1, 32);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(calls);
        Assert.InRange(started, 1, 32);
    }

    [Fact]
    public async Task DistinctReadBurstRejectsOverflowWithoutQueuingOrBypass()
    {
        var cache = new SeerrReadCache();
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<SeerrResult> Load(CancellationToken token)
        {
            Interlocked.Increment(ref started);
            await release.Task.WaitAsync(token);
            return SeerrResult.Success(System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone());
        }
        var calls = Enumerable.Range(0, 100).Select(i => cache.GetAsync("scope", Alice, 42, $"api/v1/search?query={i}", Load,
            TimeSpan.FromSeconds(2), CancellationToken.None)).ToArray();
        try
        {
            var rejected = await Task.WhenAll(calls.Where(t => t.IsCompleted)).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(68, rejected.Length);
            Assert.All(rejected, r => Assert.Equal(SeerrFailure.UpstreamUnavailable, r.Failure));
            Assert.Equal(32, started);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(calls);
        Assert.Equal(32, started);
    }

    [Fact]
    public async Task InflightPrewriteReadCannotReturnSuccessAfterPost()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("/user/jellyfin/", StringComparison.Ordinal)) return Json("{\"id\":42,\"permissions\":32}");
            if (request.Method == HttpMethod.Post) return Json("{\"id\":7,\"status\":2}", HttpStatusCode.Created);
            if (path.Contains("/movie/1", StringComparison.Ordinal)) return Json("{\"id\":1}");
            if (Interlocked.Increment(ref reads) == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return Json("{\"version\":\"before\"}");
            }
            return Json("{\"version\":\"after\"}");
        }));
        var client = new SeerrClient(http, Options(), readCache: new SeerrReadCache());
        var first = client.GetPersonalRequestsAsync(Alice, 20, 0, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = client.GetPersonalRequestsAsync(Alice, 20, 0, CancellationToken.None);
        Assert.Null((await client.CreateRequestAsync(Alice, new CreateSeerrRequest("movie", 1, null, false), CancellationToken.None)).Failure);
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(results, result => Assert.Equal("after", result.Value?.GetProperty("version").GetString()));
        Assert.Equal(2, reads);
    }
}