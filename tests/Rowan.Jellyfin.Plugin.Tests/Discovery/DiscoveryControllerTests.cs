using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Discovery;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Discovery;

public sealed class DiscoveryControllerTests
{
    private static readonly Guid Alice = Guid.Parse("f6b48a38-9e4b-4b1b-a957-e8e425e91922");
    private static readonly Guid Bob = Guid.Parse("e1bc172d-6870-4bad-8bc7-32af478474bc");

    [Fact]
    public async Task UpcomingRoutesUseMappedIdentityEndpointTypeAndBoundedPages()
    {
        var paths = new List<string>();
        using var http = Stub(request => {
            var path = request.RequestUri!.PathAndQuery;
            paths.Add(path);
            if (path.Contains("/user/jellyfin/")) return Json("{\"id\":42}");
            Assert.Equal("42", request.Headers.GetValues("X-API-User").Single());
            return Json("""
                {"page":2,"totalPages":5,"results":[{"id":71,"title":"Future film","name":"Future series","posterPath":"/fixture.jpg","mediaInfo":{"status":2}},{"id":72,"mediaType":"person","name":"Ignore"}]}
                """);
        });
        var client = new SeerrClient(http, Options());
        var controller = Controller(client, [new Claim("Jellyfin-UserId", Alice.ToString())]);
        Assert.IsType<BadRequestResult>((await controller.GetUpcomingMovies(0)).Result);
        Assert.IsType<BadRequestResult>((await controller.GetUpcomingTv(101)).Result);
        Assert.IsType<ForbidResult>((await Controller(client, []).GetUpcomingMovies()).Result);
        Assert.IsType<ForbidResult>((await Controller(client, [], _ => false).GetUpcomingTv()).Result);
        Assert.Empty(paths);
        var movie = Assert.IsType<SourceResult<CatalogItem>>(Assert.IsType<OkObjectResult>((await controller.GetUpcomingMovies(2)).Result).Value);
        var tv = Assert.IsType<SourceResult<CatalogItem>>(Assert.IsType<OkObjectResult>((await controller.GetUpcomingTv(2)).Result).Value);
        Assert.Equal("movie", Assert.Single(movie.Items).MediaType);
        Assert.Equal("tv", Assert.Single(tv.Items).MediaType);
        Assert.Equal("Future film", movie.Items[0].Title);
        Assert.Equal("Future series", tv.Items[0].Title);
        Assert.Equal(2, movie.Items[0].Status);
        Assert.Equal(2, movie.Page);
        Assert.Equal(5, tv.TotalPages);
        Assert.Contains("/seerr/api/v1/discover/movies/upcoming?page=2", paths);
        Assert.Contains("/seerr/api/v1/discover/tv/upcoming?page=2", paths);
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task UpcomingFailureIsSanitizedAndDoesNotPoisonOtherFeed()
    {
        using var http = Stub(request => {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("/user/jellyfin/")) return Json("{\"id\":42}");
            if (path.Contains("/movies/upcoming")) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return Json("{\"results\":[{\"id\":9,\"name\":\"TV still works\"}]}");
        });
        var controller = Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]);
        var movie = Assert.IsType<SourceResult<CatalogItem>>(Assert.IsType<OkObjectResult>((await controller.GetUpcomingMovies()).Result).Value);
        var tv = Assert.IsType<SourceResult<CatalogItem>>(Assert.IsType<OkObjectResult>((await controller.GetUpcomingTv()).Result).Value);
        Assert.Equal("UpstreamUnavailable", movie.Error);
        Assert.Empty(movie.Items);
        Assert.Null(tv.Error);
        Assert.Single(tv.Items);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"id\":42,\"email\":\"private@example.invalid\",\"username\":\"private-login\"}")]
    [InlineData("{\"displayName\":\"Missing identity\"}")]
    [InlineData("{\"id\":\"42\",\"displayName\":\"Invalid identity\"}")]
    [InlineData("{\"id\":42,\"displayName\":\"private@example.invalid\"}")]
    [InlineData("{\"id\":42,\"displayName\":\"  \"}")]
    [InlineData("{\"id\":42,\"displayName\":123}")]
    [InlineData("{\"id\":42,\"displayName\":\"Bad\\nLabel\"}")]
    public void MissingOrUnsafeRequesterDisplayIsUnknown(string requester)
    {
        using var json = JsonDocument.Parse("{\"results\":[{\"id\":1,\"status\":2,\"requestedBy\":" + requester + "}]}");
        var source = SeerrResult.Success(json.RootElement);
        Assert.Null(Assert.Single(DiscoveryDtos.SharedRequests(source).Items).RequesterDisplayName);
        Assert.Null(Assert.Single(DiscoveryDtos.Requests(source, 42).Items).RequesterDisplayName);
    }

    [Fact]
    public void RequesterProjectionRequiresAuthorizationAndRejectsOversizedNames()
    {
        using var json = JsonDocument.Parse("""
            {"results":[{"id":1,"status":2,"requestedBy":{"id":42,"displayName":" Synthetic Alice ","email":"private@example.invalid","token":"private-token"}}]}
            """);
        var source = SeerrResult.Success(json.RootElement);
        Assert.Null(Assert.Single(DiscoveryDtos.Requests(source).Items).RequesterDisplayName);
        Assert.Null(Assert.Single(DiscoveryDtos.Requests(source, 57).Items).RequesterDisplayName);
        Assert.Equal("Synthetic Alice", Assert.Single(DiscoveryDtos.Requests(source, 42).Items).RequesterDisplayName);
        using var oversized = JsonDocument.Parse("{\"results\":[{\"id\":1,\"status\":2,\"requestedBy\":{\"id\":42,\"displayName\":\"" + new string('a', 101) + "\"}}]}");
        Assert.Null(Assert.Single(DiscoveryDtos.SharedRequests(SeerrResult.Success(oversized.RootElement)).Items).RequesterDisplayName);
    }

    [Fact]
    public async Task PersonalRequesterNameIsBoundToFreshMappingEvenWhenUpstreamReturnsAnotherUser()
    {
        using var http = Stub(request => {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("/user/jellyfin/")) return Json($"{{\"id\":{(path.Contains(Alice.ToString()) ? 42 : 57)}}}");
            if (!path.Contains("/request?")) return Json("{\"results\":[]}");
            return Json("""
                {"results":[{"id":1,"status":2,"requestedBy":{"id":42,"displayName":"Synthetic Alice","email":"private@example.invalid","token":"private-token"}}]}
                """);
        });
        var client = new SeerrClient(http, Options());
        foreach (var user in new[] { Alice, Bob })
        {
            var result = Assert.IsType<OkObjectResult>((await Controller(client, [new Claim("Jellyfin-UserId", user.ToString())]).GetDiscovery()).Result);
            var response = Assert.IsType<DiscoveryResponse>(result.Value);
            Assert.Equal(user == Alice ? "Synthetic Alice" : null, Assert.Single(response.Requests.Items).RequesterDisplayName);
            var wire = JsonSerializer.Serialize(response);
            Assert.DoesNotContain("private", wire);
            Assert.DoesNotContain("requestedBy", wire);
            Assert.DoesNotContain("MappedRequesterId", wire);
            if (user == Bob) Assert.DoesNotContain("Synthetic Alice", wire);
        }
    }

    [Fact]
    public async Task SharedRequestsAreExplicitAndIdenticalForDisjointAuthenticatedUsers()
    {
        var paths = new List<string>();
        using var http = Stub(request => {
            paths.Add(request.RequestUri!.PathAndQuery);
            Assert.False(request.Headers.Contains("X-API-User"));
            return Json("{\"pageInfo\":{\"page\":2,\"pages\":3,\"results\":41},\"results\":[{\"id\":8,\"status\":2,\"type\":\"movie\",\"media\":{\"tmdbId\":123,\"mediaType\":\"movie\",\"status\":5,\"path\":\"private\"},\"requestedBy\":{\"id\":42,\"displayName\":\"Synthetic Alice\",\"username\":\"private-login\",\"email\":\"private@example.invalid\",\"token\":\"private-token\"},\"modifiedBy\":{\"email\":\"private\"},\"serverId\":9,\"secret\":\"private\"}]}");
        });
        var client = new SeerrClient(http, Options());
        foreach (var user in new[] { Alice, Bob }) {
            var controller = Controller(client, [new Claim("Jellyfin-UserId", user.ToString())], shared: true);
            var result = Assert.IsType<OkObjectResult>((await controller.GetSharedRequests(2, CancellationToken.None)).Result);
            var source = Assert.IsType<SourceResult<SharedRequest>>(result.Value);
            Assert.Single(source.Items);
            Assert.Equal(2, source.Page);
            Assert.Equal(3, source.TotalPages);
            var wire = JsonSerializer.Serialize(source);
            Assert.Equal("Synthetic Alice", source.Items[0].RequesterDisplayName);
            Assert.DoesNotContain("private", wire);
            Assert.DoesNotContain("serverId", wire);
            Assert.DoesNotContain("requestedBy", wire);
        }
        Assert.Equal(2, paths.Count);
        Assert.All(paths, path => Assert.Equal("/seerr/api/v1/request?take=20&skip=20", path));
    }

    [Fact]
    public async Task SharedRequestsFailClosedForDisabledAnonymousUserlessAndUnknownUsers()
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json("{\"results\":[]}"); });
        var client = new SeerrClient(http, Options());
        Assert.IsType<NotFoundResult>((await Controller(client, [new Claim("Jellyfin-UserId", Alice.ToString())]).GetSharedRequests(1, CancellationToken.None)).Result);
        foreach (var claims in new[] { Array.Empty<Claim>(), [new Claim("Jellyfin-UserId", Guid.Empty.ToString())], [new Claim("Jellyfin-UserId", Alice.ToString()), new Claim("Jellyfin-UserId", Bob.ToString())] })
            Assert.IsType<ForbidResult>((await Controller(client, claims, shared: true).GetSharedRequests(1, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await Controller(client, [new Claim("Jellyfin-UserId", Alice.ToString())], _ => false, true).GetSharedRequests(1, CancellationToken.None)).Result);
        var anonymous = Controller(client, [new Claim("Jellyfin-UserId", Alice.ToString())], shared: true);
        anonymous.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", Alice.ToString())]));
        Assert.IsType<ForbidResult>((await anonymous.GetSharedRequests()).Result);
        Assert.Equal(0, calls);
        Assert.False(new PluginConfiguration().SharedRequestsEnabled);
    }

    [Fact]
    public async Task SharedRequestsBoundPagesAndSanitizeMalformedOrOversizedUpstream()
    {
        var paths = new List<string>();
        using var http = Stub(request => { paths.Add(request.RequestUri!.PathAndQuery); return Json("{\"results\":[{\"id\":1,\"status\":1,\"media\":{\"tmdbId\":3}}]}"); });
        var controller = Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())], shared: true);
        Assert.IsType<BadRequestResult>((await controller.GetSharedRequests(0, CancellationToken.None)).Result);
        Assert.IsType<BadRequestResult>((await controller.GetSharedRequests(101, CancellationToken.None)).Result);
        var ok = Assert.IsType<OkObjectResult>((await controller.GetSharedRequests(100, CancellationToken.None)).Result);
        Assert.Single(Assert.IsType<SourceResult<SharedRequest>>(ok.Value).Items);
        Assert.Single(paths);
        Assert.EndsWith("take=20&skip=1980", paths[0]);
        using var huge = Stub(_ => Json(new string('x', 1_048_577)));
        var bad = Assert.IsType<OkObjectResult>((await Controller(new SeerrClient(huge, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())], shared: true).GetSharedRequests(1, CancellationToken.None)).Result);
        Assert.Equal("UpstreamUnavailable", Assert.IsType<SourceResult<SharedRequest>>(bad.Value).Error);
    }

    [Fact]
    public async Task SharedRequestsRejectRedirectAndBoundAdmissionWithoutQueuing()
    {
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        using var http = new HttpClient(new AsyncHandler(async (_, token) => {
            entered.Release();
            await release.WaitAsync(token);
            return Json("{\"results\":[]}");
        }));
        var client = new SeerrClient(http, Options());
        var active = Enumerable.Range(0, 4).Select(_ => client.GetSharedRequestsAsync(1, CancellationToken.None)).ToArray();
        for (var i = 0; i < 4; i++) await entered.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SeerrFailure.UpstreamUnavailable, (await client.GetSharedRequestsAsync(1, CancellationToken.None)).Failure);
        release.Release(4);
        await Task.WhenAll(active);
        Assert.All(active, task => Assert.True(task.Result.IsSuccess));
        using var redirect = Stub(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://attacker.test/key") } });
        var result = await new SeerrClient(redirect, Options()).GetSharedRequestsAsync(1, CancellationToken.None);
        Assert.Equal(SeerrFailure.UpstreamUnavailable, result.Failure);
    }

    [Fact]
    public async Task SharedRequestsDeadlineReleasesAdmissionAndCancellationPropagates()
    {
        using var http = new HttpClient(new AsyncHandler(async (_, token) => {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{\"results\":[]}");
        }));
        var client = new SeerrClient(http, Options(), TimeSpan.FromMilliseconds(50));
        Assert.Equal(SeerrFailure.UpstreamUnavailable, (await client.GetSharedRequestsAsync(1, CancellationToken.None)).Failure);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetSharedRequestsAsync(1, canceled.Token));
    }

    [Fact]
    public void RoutesAreReadOnlyAuthorizedAndDoNotAcceptIdentity()
    {
        var type = typeof(DiscoveryController);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal("3picFin", type.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.Equal("Discovery", type.GetMethod("GetDiscovery")!.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal("Search", type.GetMethod("GetSearch")!.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.All(type.GetMethods(BindingFlags.Public | BindingFlags.Instance), method =>
            Assert.DoesNotContain(method.GetParameters(), p => p.Name is "userId" or "seerrUserId"));
    }

    [Fact]
    public async Task BundleSupportsIndependentSourcePages()
    {
        var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var http = Stub(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            paths.Add(path);
            return Json(path.Contains("/user/jellyfin/", StringComparison.Ordinal) ? "{\"id\":42}" : "{\"results\":[]}");
        });
        var controller = Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]);
        Assert.IsType<OkObjectResult>((await controller.GetDiscovery(1, CancellationToken.None, moviePage: 3, tvPage: 2, requestsPage: 4)).Result);
        Assert.Equal(4, paths.Count);
        Assert.Single(paths, path => path.Contains("/user/jellyfin/", StringComparison.Ordinal));
        Assert.Single(paths, path => path.EndsWith("/discover/movies?page=3&sortBy=popularity.desc", StringComparison.Ordinal));
        Assert.Single(paths, path => path.EndsWith("/discover/tv?page=2&sortBy=popularity.desc", StringComparison.Ordinal));
        Assert.Single(paths, path => path.Contains("take=20&skip=60&requestedBy=42", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnonymousApiKeyDuplicateAndUnknownUsersCannotQuery()
    {
        var calls = 0;
        using var http = Stub(request => { calls++; return Json("{}"); });
        foreach (var claims in new[] { Array.Empty<Claim>(), [new Claim("Jellyfin-UserId", Alice.ToString()), new Claim("Jellyfin-UserId", Bob.ToString())], [new Claim("Jellyfin-UserId", Guid.Empty.ToString())] })
        {
            var controller = Controller(new SeerrClient(http, Options()), claims, _ => false);
            Assert.IsType<ForbidResult>((await controller.GetDiscovery(1, CancellationToken.None)).Result);
            Assert.IsType<ForbidResult>((await controller.GetSearch("film", 1, CancellationToken.None)).Result);
        }
        var unknown = Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())], _ => false);
        Assert.IsType<ForbidResult>((await unknown.GetSearch("film", 1, CancellationToken.None)).Result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SearchValidatesQueryAndClampsPageAndFiltersSafeFields()
    {
        var paths = new List<string>();
        using var http = Stub(request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            return Json(paths.Count == 1 ? "{\"id\":42}" : "{\"results\":[{\"id\":1,\"mediaType\":\"movie\",\"title\":\"Film\",\"releaseDate\":\"2025-01-02\",\"posterPath\":\"/safe\",\"overview\":\"Hi\",\"mediaInfo\":{\"status\":5,\"secret\":\"leak\"},\"apiKey\":\"leak\"},{\"id\":2,\"mediaType\":\"person\"},{\"id\":3,\"mediaType\":\"tv\",\"name\":\"Series\",\"firstAirDate\":\"2024-03-04\"}]}" );
        });
        var controller = Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]);
        Assert.IsType<BadRequestObjectResult>((await controller.GetSearch(" ", 1, CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetSearch(new string('x', 201), 1, CancellationToken.None)).Result);
        Assert.Empty(paths);
        var result = Assert.IsType<OkObjectResult>((await controller.GetSearch("film & tv", int.MaxValue, CancellationToken.None)).Result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("Film", json);
        Assert.Contains("Series", json);
        Assert.DoesNotContain("person", json);
        Assert.DoesNotContain("leak", json);
        Assert.DoesNotContain("apiKey", json);
        Assert.DoesNotContain("mediaInfo", json);
        Assert.Equal(2, paths.Count);
        Assert.Contains("query=film%20%26%20tv&page=100", paths[1]);
    }

    [Fact]
    public async Task BundleMapsOnceAndScopesRequestsForTwoUsers()
    {
        var calls = new System.Collections.Concurrent.ConcurrentBag<(string Path, string? User)>();
        using var http = Stub(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            calls.Add((path, request.Headers.TryGetValues("X-API-User", out var values) ? string.Join(",", values) : null));
            if (path.Contains("/user/jellyfin/")) return Json($"{{\"id\":{(path.Contains(Alice.ToString()) ? 42 : 57)}}}");
            if (path.Contains("/request?")) return Json("{\"results\":[{\"id\":12,\"status\":2,\"requestedBy\":{\"id\":999},\"secret\":\"leak\"}]}");
            return Json("{\"results\":[{\"id\":7,\"title\":\"Movie\",\"posterPath\":\"/poster\",\"secret\":\"leak\"}]}");
        });
        var client = new SeerrClient(http, Options());
        foreach (var user in new[] { Alice, Bob })
        {
            var result = Assert.IsType<OkObjectResult>((await Controller(client, [new Claim("Jellyfin-UserId", user.ToString())]).GetDiscovery(int.MaxValue, CancellationToken.None)).Result);
            var json = JsonSerializer.Serialize(result.Value);
            Assert.Contains("Movie", json);
            Assert.Contains("12", json);
            Assert.DoesNotContain("leak", json);
            Assert.DoesNotContain("requestedBy", json);
        }
        Assert.Equal(8, calls.Count);
        Assert.Equal(2, calls.Count(c => c.User is null));
        Assert.Equal(3, calls.Count(c => c.User == "42"));
        Assert.Equal(3, calls.Count(c => c.User == "57"));
        Assert.Contains(calls, c => c.User == "42" && c.Path.Contains("page=100", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.User == "42" && c.Path.Contains("take=20&skip=1980&requestedBy=42", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.User == "57" && c.Path.Contains("requestedBy=57", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BundleReportsPartialAndMappingFailuresWithoutSecrets()
    {
        using var http = Stub(request => request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/") ? Json("{\"id\":42}") :
            request.RequestUri.AbsolutePath.Contains("/tv") ? Json("upstream-secret", HttpStatusCode.InternalServerError) : Json("{\"results\":[]}"));
        var result = Assert.IsType<OkObjectResult>((await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).GetDiscovery(1, CancellationToken.None)).Result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("UpstreamUnavailable", json);
        Assert.DoesNotContain("upstream-secret", json);
        using var unmapped = Stub(_ => Json("{}", HttpStatusCode.NotFound));
        var failure = Assert.IsType<OkObjectResult>((await Controller(new SeerrClient(unmapped, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).GetDiscovery(1, CancellationToken.None)).Result);
        Assert.Contains("UserNotMapped", JsonSerializer.Serialize(failure.Value));
        var disabled = Assert.IsType<OkObjectResult>((await Controller(new SeerrClient(http, null), [new Claim("Jellyfin-UserId", Alice.ToString())]).GetDiscovery(1, CancellationToken.None)).Result);
        Assert.Contains("Disabled", JsonSerializer.Serialize(disabled.Value));
    }

    [Fact]
    public async Task MalformedCatalogAndExternalPosterNeverLeakRawData()
    {
        using var http = Stub(request => request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/") ? Json("{\"id\":42}") :
            Json("{\"results\":[null,{\"id\":9,\"mediaType\":\"movie\",\"title\":\"Film\",\"posterPath\":\"https://secret.example/path\",\"apiKey\":\"secret\"}]}"));
        var result = Assert.IsType<OkObjectResult>((await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).GetSearch("film", -9, CancellationToken.None)).Result);
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("Film", json);
        Assert.DoesNotContain("secret", json);
        Assert.Contains("\"PosterPath\":null", json);
    }

    [Fact]
    public async Task RequestsExposeBoundedIdentityWithoutInventingDisplayFieldsOrLeakingRawData()
    {
        using var http = Stub(request => request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/") ? Json("{\"id\":42}") :
            request.RequestUri.AbsolutePath.Contains("/request") ? Json("{\"pageInfo\":{\"page\":3,\"pages\":4,\"results\":55},\"results\":[{\"id\":12,\"status\":2,\"type\":\"tv\",\"is4k\":true,\"media\":{\"tmdbId\":789,\"mediaType\":\"tv\",\"status\":5,\"secret\":\"leak\"},\"seasons\":[{\"seasonNumber\":0},{\"seasonNumber\":2},{\"seasonNumber\":-1},{\"seasonNumber\":99999}],\"requestedBy\":{\"secret\":\"leak\"}},{\"id\":13,\"status\":1,\"type\":\"untrusted\",\"media\":{\"tmdbId\":-1,\"mediaType\":\"bad\",\"status\":-4},\"seasons\":[]}]}") : Json("{\"results\":[]}"));
        var result = Assert.IsType<OkObjectResult>((await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).GetDiscovery(3, CancellationToken.None)).Result);
        var json = JsonSerializer.Serialize(result.Value);
        using var parsed = JsonDocument.Parse(json);
        var requests = parsed.RootElement.GetProperty("Requests");
        Assert.Equal(3, requests.GetProperty("Page").GetInt32());
        Assert.Equal(4, requests.GetProperty("TotalPages").GetInt32());
        Assert.Equal(55, requests.GetProperty("TotalResults").GetInt32());
        var item = requests.GetProperty("Items")[0];
        Assert.Equal("tv", item.GetProperty("Type").GetString());
        Assert.Equal(789, item.GetProperty("TmdbId").GetInt32());
        Assert.Equal("tv", item.GetProperty("MediaType").GetString());
        Assert.Equal(5, item.GetProperty("MediaStatus").GetInt32());
        Assert.True(item.GetProperty("Is4k").GetBoolean());
        Assert.Equal(new[] { 0, 2 }, new[] { item.GetProperty("Seasons")[0].GetInt32(), item.GetProperty("Seasons")[1].GetInt32() });
        Assert.Equal(JsonValueKind.Null, item.GetProperty("Title").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("PosterPath").ValueKind);
        Assert.DoesNotContain("leak", json);
        Assert.DoesNotContain("requestedBy", json);
        var malformed = requests.GetProperty("Items")[1];
        Assert.Equal(JsonValueKind.Null, malformed.GetProperty("Type").ValueKind);
        Assert.Equal(JsonValueKind.Null, malformed.GetProperty("TmdbId").ValueKind);
        Assert.Equal(JsonValueKind.Null, malformed.GetProperty("MediaStatus").ValueKind);
    }

    [Fact]
    public async Task CatalogPreservesValidPaginationAndRejectsMalformedMetadata()
    {
        using var valid = Stub(request => request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/") ? Json("{\"id\":42}") : Json("{\"page\":7,\"totalPages\":19,\"totalResults\":370,\"results\":[]}"));
        var result = Assert.IsType<OkObjectResult>((await Controller(new SeerrClient(valid, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).GetSearch("film", 7, CancellationToken.None)).Result);
        var source = Assert.IsType<SourceResult<CatalogItem>>(result.Value);
        Assert.Equal(7, source.Page);
        Assert.Equal(19, source.TotalPages);
        Assert.Equal(370, source.TotalResults);
        using var invalid = Stub(request => request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/") ? Json("{\"id\":42}") : Json("{\"page\":-1,\"totalPages\":1000000,\"totalResults\":-2,\"results\":[]}"));
        var bad = Assert.IsType<OkObjectResult>((await Controller(new SeerrClient(invalid, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).GetSearch("film", 1, CancellationToken.None)).Result);
        var filtered = Assert.IsType<SourceResult<CatalogItem>>(bad.Value);
        Assert.Null(filtered.Page);
        Assert.Null(filtered.TotalPages);
        Assert.Null(filtered.TotalResults);
    }

    [Fact]
    public async Task CallerCancellationStopsBeforeUpstream()
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json("{}"); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var controller = Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.GetDiscovery(1, cancellation.Token));
        Assert.Equal(0, calls);
    }

    private static DiscoveryController Controller(SeerrClient client, Claim[] claims, Func<Guid, bool>? exists = null, bool shared = false)
    {
        var controller = new DiscoveryController(client, exists ?? (_ => true), () => shared);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, claims.Length == 0 ? null : "test")) } };
        return controller;
    }
    private static SeerrOptions Options() => SeerrOptions.FromConfiguration(new PluginConfiguration { SeerrEnabled = true, SeerrBaseUrl = "https://seerr.example/seerr", SeerrApiKey = "example-secret" })!;
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpClient Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(respond));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
}
