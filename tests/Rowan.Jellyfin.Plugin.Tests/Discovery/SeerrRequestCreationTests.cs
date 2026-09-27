using System;
using System.Collections.Generic;
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

public sealed class SeerrRequestCreationTests
{
    private static readonly Guid Alice = Guid.Parse("f6b48a38-9e4b-4b1b-a957-e8e425e91922");
    private static readonly Guid Bob = Guid.Parse("e1bc172d-6870-4bad-8bc7-32af478474bc");

    [Fact]
    public async Task RouteRequiresUserAndRejectsForgedOrExtraFieldsBeforeNetwork()
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json("{}"); });
        var client = new SeerrClient(http, Options());
        Assert.NotNull(typeof(DiscoveryController).GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal("Requests", typeof(DiscoveryController).GetMethod("CreateRequest")!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        foreach (var claims in new[] { Array.Empty<Claim>(), new[] { new Claim("Jellyfin-UserId", Alice.ToString()), new Claim("Jellyfin-UserId", Bob.ToString()) }, new[] { new Claim("Jellyfin-UserId", Guid.Empty.ToString()) } })
            Assert.IsType<ForbidResult>((await Controller(client, claims).CreateRequest(Body("{\"mediaType\":\"movie\",\"mediaId\":1}"), CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await Controller(client, [new Claim("Jellyfin-UserId", Alice.ToString())], _ => false).CreateRequest(Body("{\"mediaType\":\"movie\",\"mediaId\":1}"), CancellationToken.None)).Result);
        var valid = Controller(client, [new Claim("Jellyfin-UserId", Alice.ToString())]);
        foreach (var field in new[] { "userId", "serverId", "profileId", "rootFolder", "ignoreQuota", "tags" })
            Assert.IsType<BadRequestObjectResult>((await valid.CreateRequest(Body($"{{\"mediaType\":\"movie\",\"mediaId\":1,\"{field}\":42}}"), CancellationToken.None)).Result);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("{\"mediaType\":\"tv\",\"mediaId\":1}")]
    [InlineData("{\"mediaType\":\"tv\",\"mediaId\":1,\"seasons\":[]}")]
    [InlineData("{\"mediaType\":\"tv\",\"mediaId\":1,\"seasons\":[0]}")]
    [InlineData("{\"mediaType\":\"tv\",\"mediaId\":1,\"seasons\":[2,2]}")]
    [InlineData("{\"mediaType\":\"tv\",\"mediaId\":1,\"seasons\":\"all\"}")]
    [InlineData("{\"mediaType\":\"movie\",\"mediaId\":1,\"seasons\":[1]}")]
    [InlineData("{\"mediaType\":\"movie\",\"mediaId\":0}")]
    [InlineData("{\"mediaType\":\"movie\",\"mediaId\":\"9\"}")]
    [InlineData("{\"mediaType\":\"tv\",\"mediaId\":1,\"seasons\":[\"2\"]}")]
    [InlineData("{\"mediaType\":\"movie\",\"mediaId\":9,\"mediaId\":10}")]
    [InlineData("{\"mediaType\":\"show\",\"mediaId\":1}")]
    public async Task InvalidSelectionNeverContactsSeerr(string body)
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json("{}"); });
        Assert.IsType<BadRequestObjectResult>((await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).CreateRequest(Body(body), CancellationToken.None)).Result);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("movie", false, 32, true)]
    [InlineData("movie", true, 32, false)]
    [InlineData("movie", true, 2048, true)]
    [InlineData("tv", false, 524288, true)]
    [InlineData("tv", true, 524288, false)]
    [InlineData("tv", true, 4096, true)]
    public async Task PermissionsAreCheckedForMappedUserNotApiKeyAdmin(string type, bool fourK, int permissions, bool allowed)
    {
        var calls = new List<(HttpMethod Method, string? User)>();
        using var http = Stub(request =>
        {
            calls.Add((request.Method, Header(request, "X-API-User")));
            return Json(request.Method == HttpMethod.Get ? request.RequestUri!.AbsolutePath.Contains("/tv/") ? "{\"id\":9,\"seasons\":[{\"seasonNumber\":2}]}" : $"{{\"id\":42,\"permissions\":{permissions}}}" : "{\"id\":91,\"status\":1}", request.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.Created);
        });
        var body = type == "tv" ? $"{{\"mediaType\":\"tv\",\"mediaId\":9,\"seasons\":[2],\"is4k\":{fourK.ToString().ToLowerInvariant()}}}" : $"{{\"mediaType\":\"movie\",\"mediaId\":9,\"is4k\":{fourK.ToString().ToLowerInvariant()}}}";
        var result = await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).CreateRequest(Body(body), CancellationToken.None);
        if (allowed) { Assert.IsType<CreatedResult>(result.Result); Assert.Equal(type == "tv" ? 3 : 2, calls.Count); Assert.Equal("42", calls[^1].User); }
        else { Assert.IsType<StatusCodeResult>(result.Result); Assert.Equal(403, ((StatusCodeResult)result.Result!).StatusCode); Assert.Single(calls); }
        Assert.Null(calls[0].User);
    }

    [Fact]
    public async Task PostCarriesOnlyDefaultsSelectionAndIdentityForEachUser()
    {
        var calls = new List<(string Uri, string? User, string? Key, string? Body)>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls.Add((request.RequestUri!.ToString(), Header(request, "X-API-User"), Header(request, "X-Api-Key"), request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return Json(request.Method == HttpMethod.Get ? request.RequestUri.AbsolutePath.Contains("/tv/") ? "{\"id\":123,\"seasons\":[{\"seasonNumber\":1},{\"seasonNumber\":3}]}" : $"{{\"id\":{(request.RequestUri.AbsolutePath.EndsWith(Alice.ToString(), StringComparison.Ordinal) ? 42 : 57)},\"permissions\":2}}" : "{\"id\":91,\"status\":2,\"secret\":\"upstream-secret\"}", request.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.Created);
        }));
        var client = new SeerrClient(http, Options());
        foreach (var id in new[] { Alice, Bob })
        {
            var result = Assert.IsType<CreatedResult>((await Controller(client, [new Claim("Jellyfin-UserId", id.ToString())]).CreateRequest(Body("{\"mediaType\":\"tv\",\"mediaId\":123,\"seasons\":[1,3],\"is4k\":true}"), CancellationToken.None)).Result);
            Assert.DoesNotContain("secret", JsonSerializer.Serialize(result.Value));
        }
        Assert.Equal(new string?[] { null, "42", "42", null, "57", "57" }, calls.ConvertAll(c => c.User));
        Assert.Equal("https://seerr.example/seerr/api/v1/request", calls[2].Uri);
        Assert.Equal("{\"mediaType\":\"tv\",\"mediaId\":123,\"seasons\":[1,3],\"is4k\":true}", calls[2].Body);
        Assert.Equal(calls[2].Body, calls[5].Body);
        Assert.All(calls, c => Assert.Equal("example-secret", c.Key));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, 409)]
    [InlineData(HttpStatusCode.Accepted, 409)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.Redirect, 502)]
    [InlineData(HttpStatusCode.InternalServerError, 502)]
    public async Task DuplicateNoSeasonsAndFailuresAreSanitized(HttpStatusCode status, int expected)
    {
        var calls = 0;
        using var http = Stub(request =>
        {
            calls++;
            return request.Method == HttpMethod.Get ? Json("{\"id\":42,\"permissions\":32}") : Json("private-upstream-secret", status);
        });
        var result = await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).CreateRequest(Body("{\"mediaType\":\"movie\",\"mediaId\":9}"), CancellationToken.None);
        Assert.Equal(expected, Assert.IsType<StatusCodeResult>(result.Result).StatusCode);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("{\"id\":42}", 502)]
    [InlineData("{\"id\":42,\"permissions\":0}", 403)]
    [InlineData("{\"id\":0,\"permissions\":2}", 502)]
    public async Task IncompleteOrUnauthorizedMappingNeverPosts(string mapping, int status)
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json(mapping); });
        var result = await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).CreateRequest(Body("{\"mediaType\":\"movie\",\"mediaId\":9}"), CancellationToken.None);
        Assert.Equal(status, Assert.IsType<StatusCodeResult>(result.Result).StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task MissingUserMappingNeverPosts()
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json("private-secret", HttpStatusCode.NotFound); });
        var result = await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).CreateRequest(Body("{\"mediaType\":\"movie\",\"mediaId\":9}"), CancellationToken.None);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(result.Result).StatusCode);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{\"id\":0,\"status\":2}")]
    [InlineData("{\"id\":1,\"status\":\"2\"}")]
    [InlineData("private-secret")]
    public async Task InvalidCreatedBodyIsNeverReturned(string created)
    {
        using var http = Stub(request => request.Method == HttpMethod.Get ? Json("{\"id\":42,\"permissions\":32}") : Json(created, HttpStatusCode.Created));
        var result = await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).CreateRequest(Body("{\"mediaType\":\"movie\",\"mediaId\":9}"), CancellationToken.None);
        Assert.Equal(502, Assert.IsType<StatusCodeResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task CallerCancellationBeforeMappingNeverPosts()
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json("{}"); });
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var controller = Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.CreateRequest(Body("{\"mediaType\":\"movie\",\"mediaId\":9}"), cancel.Token));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("[999]", 400)]
    [InlineData("[1,999]", 400)]

    public async Task TvSeasonsMustAllExistInServerFetchedShow(string seasons, int expected)
    {
        var paths = new List<string>();
        using var http = Stub(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Json(paths.Count == 1 ? "{\"id\":42,\"permissions\":524288}" : "{\"id\":9,\"seasons\":[{\"seasonNumber\":0},{\"seasonNumber\":1},{\"seasonNumber\":3}]}");
        });
        var result = await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).CreateRequest(Body($"{{\"mediaType\":\"tv\",\"mediaId\":9,\"seasons\":{seasons}}}"), CancellationToken.None);
        Assert.Equal(expected, Assert.IsType<BadRequestObjectResult>(result.Result).StatusCode);
        Assert.Equal(2, paths.Count);
        Assert.EndsWith("/api/v1/tv/9", paths[1]);
    }

    [Fact]
    public async Task TvValidationFailureOrCsrfNeverPosts()
    {
        foreach (var (detail, expected) in new[] { ("{}", 502), ("{\"id\":9,\"seasons\":[]}", 400) })
        {
            var calls = 0;
            using var http = Stub(request => { calls++; return Json(calls == 1 ? "{\"id\":42,\"permissions\":524288}" : detail); });
            var result = await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).CreateRequest(Body("{\"mediaType\":\"tv\",\"mediaId\":9,\"seasons\":[1]}"), CancellationToken.None);
            Assert.Equal(expected, result.Result switch { StatusCodeResult status => status.StatusCode, BadRequestObjectResult bad => bad.StatusCode, _ => 0 });
            Assert.Equal(2, calls);
        }
        var csrfCalls = 0;
        using var csrfHttp = Stub(request =>
        {
            csrfCalls++;
            var response = Json(csrfCalls == 1 ? "{\"id\":42,\"permissions\":524288}" : "{\"id\":9,\"seasons\":[{\"seasonNumber\":1}]}");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "XSRF-TOKEN=token; Path=/");
            return response;
        });
        var csrfResult = await Controller(new SeerrClient(csrfHttp, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).CreateRequest(Body("{\"mediaType\":\"tv\",\"mediaId\":9,\"seasons\":[1]}"), CancellationToken.None);
        var csrfFailure = Assert.IsType<ObjectResult>(csrfResult.Result);
        Assert.Equal(502, csrfFailure.StatusCode);
        Assert.Contains("CSRF", Assert.IsType<string>(csrfFailure.Value));
        Assert.Equal(1, csrfCalls);
    }

    [Fact]
    public async Task MixedPreviouslyRequestedSeasonsAreLeftForSeerrToFilter()
    {
        var paths = new List<string>();
        using var http = Stub(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post) return Json("{\"id\":91,\"status\":1}", HttpStatusCode.Created);
            return Json(paths.Count == 1 ? "{\"id\":42,\"permissions\":524288}" : "{\"id\":9,\"seasons\":[{\"seasonNumber\":1},{\"seasonNumber\":3}],\"mediaInfo\":{\"requests\":[{\"seasons\":[{\"seasonNumber\":1}]}]}}");
        });
        var result = await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).CreateRequest(Body("{\"mediaType\":\"tv\",\"mediaId\":9,\"seasons\":[1,3]}"), CancellationToken.None);
        Assert.IsType<CreatedResult>(result.Result);
        Assert.Equal(3, paths.Count);
    }

    [Fact]
    public async Task CsrfEnabledMovieFailsBeforePost()
    {
        var calls = 0;
        using var http = Stub(_ =>
        {
            calls++;
            var response = Json("{\"id\":42,\"permissions\":32}");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "XSRF-TOKEN=token; Path=/");
            return response;
        });
        var result = await Controller(new SeerrClient(http, Options()), [new Claim("Jellyfin-UserId", Alice.ToString())]).CreateRequest(Body("{\"mediaType\":\"movie\",\"mediaId\":9}"), CancellationToken.None);
        Assert.Contains("CSRF", Assert.IsType<string>(Assert.IsType<ObjectResult>(result.Result).Value));
        Assert.Equal(1, calls);
    }

    private static JsonElement Body(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static DiscoveryController Controller(SeerrClient client, Claim[] claims, Func<Guid, bool>? exists = null) => new(client, exists ?? (_ => true))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, claims.Length == 0 ? null : "test")) } }
    };
    private static SeerrOptions Options() => SeerrOptions.FromConfiguration(new PluginConfiguration { SeerrEnabled = true, SeerrBaseUrl = "https://seerr.example/seerr", SeerrApiKey = "example-secret" })!;
    private static string? Header(HttpRequestMessage request, string key) => request.Headers.TryGetValues(key, out var values) ? string.Join(",", values) : null;
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpClient Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler((request, _) => Task.FromResult(respond(request))));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
}
