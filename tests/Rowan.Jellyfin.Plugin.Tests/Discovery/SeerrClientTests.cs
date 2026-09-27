using System;
using System.Collections.Generic;
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

public sealed class SeerrClientTests
{
    private static readonly Guid JellyfinUser = Guid.Parse("f6b48a38-9e4b-4b1b-a957-e8e425e91922");

    [Fact]
    public async Task MapsUserBeforeReadAndSendsMappedIdentity()
    {
        var requests = new List<(string Uri, string? Key, string? User)>();
        using var http = Stub(request =>
        {
            requests.Add((request.RequestUri!.ToString(), Header(request, "X-Api-Key"), Header(request, "X-API-User")));
            return Json(requests.Count == 1 ? "{\"id\":42}" : "{\"results\":[]}");
        });
        var client = new SeerrClient(http, Options());

        var result = await client.GetUserReadAsync(JellyfinUser, "api/v1/discover/movies?page=2", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, requests.Count);
        Assert.Equal($"https://seerr.example/seerr/api/v1/user/jellyfin/{JellyfinUser:D}", requests[0].Uri);
        Assert.Equal("https://seerr.example/seerr/api/v1/discover/movies?page=2", requests[1].Uri);
        Assert.All(requests, r => Assert.Equal("example-secret", r.Key));
        Assert.Null(requests[0].User);
        Assert.Equal("42", requests[1].User);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}", SeerrFailure.UserNotMapped)]
    [InlineData(HttpStatusCode.OK, "{\"id\":0}", SeerrFailure.InvalidMapping)]
    [InlineData(HttpStatusCode.OK, "{\"id\":\"42\"}", SeerrFailure.InvalidMapping)]
    [InlineData(HttpStatusCode.OK, "{\"id\":-2}", SeerrFailure.InvalidMapping)]
    [InlineData(HttpStatusCode.OK, "bad-json-secret", SeerrFailure.InvalidMapping)]
    public async Task InvalidMappingNeverCallsCatalog(HttpStatusCode status, string body, SeerrFailure expected)
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json(body, status); });
        var result = await new SeerrClient(http, Options()).GetUserReadAsync(JellyfinUser, "api/v1/search?query=film", CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpstreamErrorsDoNotExposeBodyKeyOrUrl()
    {
        using var http = Stub(_ => Json("upstream-secret-body", HttpStatusCode.InternalServerError));
        var result = await new SeerrClient(http, Options()).GetUserReadAsync(JellyfinUser, "api/v1/search?query=film", CancellationToken.None);
        Assert.Equal(SeerrFailure.UpstreamUnavailable, result.Failure);
        Assert.DoesNotContain("upstream-secret-body", result.ToString());
        Assert.DoesNotContain("example-secret", result.ToString());
        Assert.DoesNotContain("seerr.example", result.ToString());
    }

    [Fact]
    public async Task RedirectIsNotFollowedByTransport()
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://elsewhere.example/") } }; });
        var result = await new SeerrClient(http, Options()).GetUserReadAsync(JellyfinUser, "api/v1/search", CancellationToken.None);
        Assert.Equal(SeerrFailure.UpstreamUnavailable, result.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CallerCancellationIsPropagated()
    {
        using var http = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json("{}"); }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SeerrClient(http, Options()).GetUserReadAsync(JellyfinUser, "api/v1/search", cancellation.Token));
    }

    [Fact]
    public async Task RequestTimeoutReturnsSanitizedFailure()
    {
        using var http = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json("{}"); }));
        var result = await new SeerrClient(http, Options(), TimeSpan.FromMilliseconds(30))
            .GetUserReadAsync(JellyfinUser, "api/v1/search", CancellationToken.None);
        Assert.Equal(SeerrFailure.UpstreamUnavailable, result.Failure);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedWithoutEcho()
    {
        using var http = Stub(_ => Json(new string('x', 1_100_000)));
        var result = await new SeerrClient(http, Options()).GetUserReadAsync(JellyfinUser, "api/v1/search", CancellationToken.None);
        Assert.Equal(SeerrFailure.UpstreamUnavailable, result.Failure);
    }

    [Fact]
    public async Task SeparateUsersAreMappedIndependently()
    {
        var requests = new List<(string Path, string? User)>();
        using var http = Stub(request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, Header(request, "X-API-User")));
            return Json(request.RequestUri.AbsolutePath.Contains("/user/jellyfin/", StringComparison.Ordinal)
                ? $"{{\"id\":{(requests.Count == 1 ? 42 : 57)}}}"
                : "{}");
        });
        var client = new SeerrClient(http, Options());
        Assert.True((await client.GetUserReadAsync(JellyfinUser, "api/v1/search", CancellationToken.None)).IsSuccess);
        Assert.True((await client.GetUserReadAsync(Guid.NewGuid(), "api/v1/search", CancellationToken.None)).IsSuccess);
        Assert.Equal(new string?[] { null, "42", null, "57" }, requests.ConvertAll(r => r.User));
    }

    [Fact]
    public async Task PersonalRequestsBindEachMappedUserToHeaderAndFilter()
    {
        var other = Guid.Parse("e1bc172d-6870-4bad-8bc7-32af478474bc");
        var requests = new List<(string Uri, string? User, string? Key)>();
        using var http = Stub(request =>
        {
            requests.Add((request.RequestUri!.ToString(), Header(request, "X-API-User"), Header(request, "X-Api-Key")));
            if (request.RequestUri.AbsolutePath.EndsWith($"/{JellyfinUser:D}", StringComparison.Ordinal)) return Json("{\"id\":42}");
            if (request.RequestUri.AbsolutePath.EndsWith($"/{other:D}", StringComparison.Ordinal)) return Json("{\"id\":57}");
            return Json("{\"results\":[]}");
        });
        var client = new SeerrClient(http, Options());

        Assert.True((await client.GetPersonalRequestsAsync(JellyfinUser, 12, 3, CancellationToken.None)).IsSuccess);
        Assert.True((await client.GetPersonalRequestsAsync(other, 8, 9, CancellationToken.None)).IsSuccess);
        Assert.Equal(new[]
        {
            ($"https://seerr.example/seerr/api/v1/user/jellyfin/{JellyfinUser:D}", (string?)null),
            ("https://seerr.example/seerr/api/v1/request?take=12&skip=3&requestedBy=42", "42"),
            ($"https://seerr.example/seerr/api/v1/user/jellyfin/{other:D}", (string?)null),
            ("https://seerr.example/seerr/api/v1/request?take=8&skip=9&requestedBy=57", "57")
        }, requests.ConvertAll(r => (r.Uri, r.User)));
        Assert.All(requests, r => Assert.Equal("example-secret", r.Key));
    }

    [Fact]
    public async Task PersonalRequestsClampPagination()
    {
        var paths = new List<string>();
        using var http = Stub(request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            return Json(request.RequestUri.AbsolutePath.Contains("/user/jellyfin/", StringComparison.Ordinal) ? "{\"id\":42}" : "{}");
        });
        var client = new SeerrClient(http, Options());
        Assert.True((await client.GetPersonalRequestsAsync(JellyfinUser, int.MaxValue, -1, CancellationToken.None)).IsSuccess);
        Assert.True((await client.GetPersonalRequestsAsync(JellyfinUser, -1, int.MaxValue, CancellationToken.None)).IsSuccess);
        Assert.Equal("/seerr/api/v1/request?take=100&skip=0&requestedBy=42", paths[1]);
        Assert.Equal("/seerr/api/v1/request?take=1&skip=10000&requestedBy=42", paths[3]);
    }

    [Theory]
    [InlineData("{\"results\":[{\"requestedBy\":{\"id\":57}}]}", false)]
    [InlineData("{\"results\":[{\"requestedBy\":{\"id\":42}}]}", true)]
    [InlineData("{\"results\":[{}]}", false)]
    [InlineData("{\"results\":[{\"requestedBy\":{\"id\":\"42\"}}]}", false)]
    [InlineData("{\"results\":[{\"requestedBy\":{\"id\":42}},{\"requestedBy\":{\"id\":57}}]}", false)]
    [InlineData("{\"results\":{}}", false)]
    public async Task PersonalHomeReadVerifiesRequesterAndFailsClosed(string upstream, bool valid)
    {
        var calls = new List<(string Path, string? ActingUser)>();
        using var http = Stub(request => {
            calls.Add((request.RequestUri!.PathAndQuery, Header(request, "X-API-User")));
            return Json(calls.Count == 1 ? "{\"id\":42}" : upstream);
        });
        var result = await new SeerrClient(http, Options()).GetPersonalAvailableRequestsAsync(JellyfinUser, CancellationToken.None);
        Assert.Equal(valid, result.IsSuccess);
        Assert.Equal(2, calls.Count);
        Assert.Null(calls[0].ActingUser);
        Assert.Equal("42", calls[1].ActingUser);
        Assert.Equal("/seerr/api/v1/request?take=100&skip=0&requestedBy=42", calls[1].Path);
        if (!valid) Assert.Equal(SeerrFailure.UpstreamUnavailable, result.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}", SeerrFailure.UserNotMapped)]
    [InlineData(HttpStatusCode.OK, "{\"id\":0}", SeerrFailure.InvalidMapping)]
    [InlineData(HttpStatusCode.InternalServerError, "secret-body", SeerrFailure.UpstreamUnavailable)]
    public async Task PersonalRequestsPreserveTypedMappingFailures(HttpStatusCode status, string body, SeerrFailure expected)
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json(body, status); });
        var result = await new SeerrClient(http, Options()).GetPersonalRequestsAsync(JellyfinUser, 10, 0, CancellationToken.None);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PersonalRequestsDoNotMapEmptyUser()
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json("{}"); });
        var result = await new SeerrClient(http, Options()).GetPersonalRequestsAsync(Guid.Empty, 10, 0, CancellationToken.None);
        Assert.Equal(SeerrFailure.UserNotMapped, result.Failure);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PersonalRequestReadFailureIsSanitized()
    {
        var calls = 0;
        using var http = Stub(_ => Json(++calls == 1 ? "{\"id\":42}" : "private-upstream-secret", calls == 1 ? HttpStatusCode.OK : HttpStatusCode.Forbidden));
        var result = await new SeerrClient(http, Options()).GetPersonalRequestsAsync(JellyfinUser, 10, 0, CancellationToken.None);
        Assert.Equal(SeerrFailure.UpstreamUnavailable, result.Failure);
        Assert.Equal(2, calls);
        Assert.Null(result.Value);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("seerr.example", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StalledMovieReadDoesNotBlockTvAndRequestsOrLoseTheirResults()
    {
        var movieStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var othersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paths = new System.Collections.Concurrent.ConcurrentBag<(string Path, string? User)>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            paths.Add((path, Header(request, "X-API-User")));
            if (path.Contains("/user/jellyfin/", StringComparison.Ordinal)) return Json("{\"id\":42}");
            if (path.Contains("/movies", StringComparison.Ordinal))
            {
                movieStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Json("{}");
            }
            if (path.Contains("/request?", StringComparison.Ordinal) && paths.Any(p => p.Path.Contains("/tv", StringComparison.Ordinal))) othersStarted.TrySetResult();
            return Json("{\"results\":[]}");
        }));
        var client = new SeerrClient(http, Options(), TimeSpan.FromMilliseconds(500));
        var pending = client.GetDiscoveryAsync(JellyfinUser, 2, 20, CancellationToken.None);
        await movieStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await othersStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
        var bundle = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SeerrFailure.UpstreamUnavailable, bundle.Movies.Failure);
        Assert.True(bundle.Tv.IsSuccess);
        Assert.True(bundle.Requests.IsSuccess);
        Assert.Single(paths, p => p.Path.Contains("/user/jellyfin/", StringComparison.Ordinal));
        Assert.All(paths.Where(p => !p.Path.Contains("/user/jellyfin/", StringComparison.Ordinal)), p => Assert.Equal("42", p.User));
        Assert.Contains(paths, p => p.Path.Contains("take=20&skip=20&requestedBy=42", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://elsewhere.example/api/v1/search")]
    [InlineData("/api/v1/search")]
    [InlineData("api/v1/user/jellyfin/123")]
    [InlineData("api/v1/search/../request")]
    [InlineData("api/v1/search#fragment")]
    [InlineData("api/v1/request")]
    [InlineData("api/v1/request?take=10&skip=0")]
    [InlineData("api/v1/request?take=10&skip=0&requestedBy=57")]
    public async Task UnsafePathRejectedBeforeSending(string path)
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json("{}"); });
        await Assert.ThrowsAsync<ArgumentException>(() => new SeerrClient(http, Options()).GetUserReadAsync(JellyfinUser, path, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    private static string? Header(HttpRequestMessage request, string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpClient Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler((request, _) => Task.FromResult(respond(request))));
    private static SeerrOptions Options() => SeerrOptions.FromConfiguration(new PluginConfiguration { SeerrEnabled = true, SeerrBaseUrl = "https://seerr.example/seerr", SeerrApiKey = "example-secret" })!;
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
