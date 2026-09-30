using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Discovery;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Discovery;

public sealed class TitleDetailsTests
{
    private static readonly Guid Alice = Guid.Parse("f6b48a38-9e4b-4b1b-a957-e8e425e91922");
    private static readonly Guid Bob = Guid.Parse("e1bc172d-6870-4bad-8bc7-32af478474bc");
    private static readonly Guid Item = Guid.Parse("731b21f6-0c54-4f0f-8a4d-f92389b460dd");

    [Fact]
    public async Task MappedDetailIsBoundedAllowlistedAndLocallyScoped()
    {
        var calls = new List<(string Path, string? User)>();
        using var http = new HttpClient(new Handler(req => {
            var path = req.RequestUri!.PathAndQuery;
            var user = req.Headers.TryGetValues("X-API-User", out var values) ? string.Join(",", values) : null;
            calls.Add((path, user));
            if (path.Contains("/user/jellyfin/")) return Json($"{{\"id\":{(path.Contains(Alice.ToString()) ? 42 : 57)},\"permissions\":528384}}");
            return Json($$"""{"id":9,"name":"Show","overview":"Plot","firstAirDate":"2024-03-04","posterPath":"/poster.jpg","seasons":[{"seasonNumber":0},{"seasonNumber":1}],"mediaInfo":{"status":5,"jellyfinMediaId":"{{Item:D}}"},"secret":"do-not-leak"}""");
        }));
        var client = Client(http);
        foreach (var (user, expected) in new[] { (Alice, Item), (Bob, (Guid?)null) })
        {
            var controller = Controller(client, user, (id, type, tmdb, hint) => id == Alice && type == "tv" && tmdb == 9 && hint == Item ? expected : null);
            var result = Assert.IsType<OkObjectResult>((await controller.GetTitleDetails("tv", 9, CancellationToken.None)).Result);
            var dto = Assert.IsType<TitleDetails>(result.Value);
            Assert.Equal(expected, dto.LibraryItemId);
            Assert.Equal("Show", dto.Title);
            Assert.Equal(new[] { 1 }, dto.Seasons);
            Assert.Equal(5, dto.MediaStatus);
            Assert.False(dto.CanRequest); // Seerr reports all standard seasons available; Open remains independent.
            Assert.True(dto.CanRequest4k);
            Assert.Equal("2024-03-04", dto.Date);
            Assert.Equal("private, no-store", controller.Response.Headers.CacheControl);
            var wire = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Contains("\"LibraryItemId\"", wire);
            Assert.Contains("\"CanRequest\":false", wire);
            Assert.Contains("\"Date\":\"2024-03-04\"", wire);
            Assert.DoesNotContain("do-not-leak", wire);
            Assert.DoesNotContain("jellyfinMediaId", wire);
            Assert.DoesNotContain("Path\":\"https", wire);
        }
        Assert.Equal(4, calls.Count);
        Assert.Equal(("/seerr/api/v1/tv/9", "42"), calls[1]);
        Assert.Equal(("/seerr/api/v1/tv/9", "57"), calls[3]);
    }

    [Fact]
    public async Task LibraryLookupFailureRetainsDetailsButNeverClaimsAbsenceOrLeaksHint()
    {
        using var http = new HttpClient(new Handler(req => Json(req.RequestUri!.AbsolutePath.Contains("/user/jellyfin/")
            ? "{\"id\":42,\"permissions\":32}"
            : "{\"id\":9,\"title\":\"Fixture\",\"mediaInfo\":{\"status\":5}}")));
        var controller = Controller(Client(http), Alice, (_, _, _, _) => throw new InvalidOperationException("private diagnostic"));
        var response = Assert.IsType<OkObjectResult>((await controller.GetTitleDetails("movie", 9, CancellationToken.None)).Result);
        var value = Assert.IsType<TitleDetails>(response.Value);
        Assert.Null(value.LibraryItemId);
        Assert.Equal("unavailable", value.LibraryStatus);
        Assert.Equal(5, value.MediaStatus);
        Assert.DoesNotContain("private diagnostic", JsonSerializer.Serialize(value));
    }

    [Fact]
    public void LibraryResolutionSelectsDeterministicVisibleTypedTmdbMatchDespiteStaleHint()
    {
        var own = new TitleLibraryCandidate(Item, "movie", "9", true);
        var foreign = new TitleLibraryCandidate(Guid.NewGuid(), "movie", "9", false);
        var otherType = new TitleLibraryCandidate(Guid.NewGuid(), "tv", "9", true);
        Assert.Equal(Item, TitleLibraryPolicy.Resolve([own, foreign, otherType], "movie", 9, Item));
        Assert.Equal(Item, TitleLibraryPolicy.Resolve([own, foreign], "movie", 9, null));
        Assert.Null(TitleLibraryPolicy.Resolve([foreign], "movie", 9, foreign.Id));
        Assert.Null(TitleLibraryPolicy.Resolve([own], "tv", 9, Item));
        Assert.Null(TitleLibraryPolicy.Resolve([own], "movie", 10, Item));
        Assert.Equal(Item, TitleLibraryPolicy.Resolve([own], "movie", 9, foreign.Id));
        Assert.Equal(Item, TitleLibraryPolicy.Resolve([own, own with { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") }], "movie", 9, null));
    }

    [Fact]
    public void RestrictedParentOrDisjointUserCannotAdvertiseLocalPlayback()
    {
        var alice = new TitleLibraryCandidate(Item, "movie", "9", true);
        var bob = new TitleLibraryCandidate(Guid.NewGuid(), "movie", "9", true);
        // Eligibility is computed from the current user's root, item, and *every* intermediate parent.
        Assert.Equal(Item, TitleLibraryPolicy.Resolve([alice, bob with { Visible = false }], "movie", 9, Item));
        Assert.Equal(bob.Id, TitleLibraryPolicy.Resolve([alice with { Visible = false }, bob], "movie", 9, bob.Id));
        Assert.Null(TitleLibraryPolicy.Resolve([alice with { Visible = false }, bob with { Visible = false }], "movie", 9, Item));
        Assert.Null(TitleLibraryPolicy.Resolve([alice with { Visible = false }], "movie", 9, Item));
    }

    [Fact]
    public void RevocationBetweenScanAndReturnDoesNotAdvertiseLocalPlayback()
    {
        var root = Guid.NewGuid();
        var candidate = new TitleLibraryCandidate(Item, "tv", "9", true, root);
        var visible = true;
        Assert.Equal(Item, TitleLibraryPolicy.Resolve([candidate], "tv", 9, Item, _ => visible));
        visible = false; // Visibility was revoked after the query's visible candidate snapshot.
        Assert.Null(TitleLibraryPolicy.Resolve([candidate], "tv", 9, Item, _ => visible));
    }

    [Fact]
    public async Task InvalidIdentityAndIdNeverReachUpstream()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Json("{}"); }));
        Assert.IsType<ForbidResult>((await Controller(Client(http), Guid.Empty, (_, _, _, _) => Item).GetTitleDetails("movie", 9, CancellationToken.None)).Result);
        Assert.IsType<BadRequestResult>((await Controller(Client(http), Alice, (_, _, _, _) => Item).GetTitleDetails("other", 9, CancellationToken.None)).Result);
        Assert.IsType<BadRequestResult>((await Controller(Client(http), Alice, (_, _, _, _) => Item).GetTitleDetails("movie", 0, CancellationToken.None)).Result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MismatchedIdentityAndUnsafePosterFailClosed()
    {
        using var http = new HttpClient(new Handler(req => Json(req.RequestUri!.AbsolutePath.Contains("/user/jellyfin/")
            ? "{\"id\":42,\"permissions\":32}"
            : "{\"id\":10,\"title\":\"Wrong\",\"posterPath\":\"https://evil.test/x\",\"mediaInfo\":{\"jellyfinMediaId\":\"../../private\"}}")));
        var controller = Controller(Client(http), Alice, (_, _, _, _) => Item);
        Assert.IsType<StatusCodeResult>((await controller.GetTitleDetails("movie", 9, CancellationToken.None)).Result);
    }

    private static SeerrClient Client(HttpClient http) => new(http, SeerrOptions.FromConfiguration(new PluginConfiguration { SeerrEnabled = true, SeerrBaseUrl = "https://seerr.example/seerr", SeerrApiKey = "secret" }));
    private static TitleDetailsController Controller(SeerrClient client, Guid user, Func<Guid, string, int, Guid?, Guid?> resolve)
    {
        var controller = new TitleDetailsController(client, id => id == user, (id, type, tmdb, hint) => { var match = resolve(id, type, tmdb, hint); return new(match, match is null ? "unknown" : "present"); });
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(user == Guid.Empty ? [] : [new Claim("Jellyfin-UserId", user.ToString())], "test")) } };
        return controller;
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
}
