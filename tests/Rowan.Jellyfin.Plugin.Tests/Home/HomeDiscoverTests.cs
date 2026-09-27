using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class HomeDiscoverTests
{
    private static readonly Guid Alice = Guid.Parse("f6b48a38-9e4b-4b1b-a957-e8e425e91922");
    private static readonly Guid Bob = Guid.Parse("e1bc172d-6870-4bad-8bc7-32af478474bc");
    private static SeerrOptions Options() => SeerrOptions.FromConfiguration(new PluginConfiguration { SeerrEnabled = true, SeerrBaseUrl = "https://seerr.example/seerr", SeerrApiKey = "test-secret" })!;
    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
    private static HomeDiscoverController Controller(SeerrClient client, PluginConfiguration flags, Claim[]? claims = null, Func<Guid, bool>? exists = null)
    {
        var controller = new HomeDiscoverController(client, exists ?? (_ => true), () => flags);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims ?? [new Claim("Jellyfin-UserId", Alice.ToString())], "test")) } };
        return controller;
    }

    [Fact]
    public async Task IndependentDefaultOffFlagsAndIdentityFailClosedBeforeNetwork()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json("{}")); }));
        var client = new SeerrClient(http, Options());
        var flags = new PluginConfiguration { HomeEnabled = true, DiscoverMoviesRowEnabled = true };
        Assert.False(new PluginConfiguration().DiscoverRowEnabled);
        Assert.False(new PluginConfiguration().DiscoverMoviesRowEnabled);
        Assert.False(new PluginConfiguration().DiscoverTvRowEnabled);
        var controller = Controller(client, flags);
        Assert.IsType<NotFoundResult>((await controller.GetDiscover(1)).Result);
        Assert.IsType<NotFoundResult>((await controller.GetDiscoverTv(1)).Result);
        foreach (var claims in new[] { Array.Empty<Claim>(), [new Claim("Jellyfin-UserId", Alice.ToString()), new Claim("Jellyfin-UserId", Bob.ToString())] })
            Assert.IsType<ForbidResult>((await Controller(client, flags, claims).GetDiscoverMovies(1)).Result);
        Assert.IsType<ForbidResult>((await Controller(client, flags, exists: _ => false).GetDiscoverMovies(1)).Result);
        flags.HomeEnabled = false;
        Assert.IsType<NotFoundResult>((await controller.GetDiscoverMovies(1)).Result);
        Assert.Equal(0, calls);
        Assert.NotNull(typeof(HomeDiscoverController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Single());
    }

    [Fact]
    public async Task EachEndpointUsesItsOwnMappedCatalogAndStrictlyFiltersAdultAndBlocklisted()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler((request, _) => {
            var path = request.RequestUri!.PathAndQuery;
            paths.Add(path);
            if (path.Contains("/user/jellyfin/")) { Assert.False(request.Headers.Contains("X-API-User")); return Task.FromResult(Json("{\"id\":42}")); }
            Assert.Equal("42", request.Headers.GetValues("X-API-User").Single());
            if (path.EndsWith("/api/v1/tv/5")) return Task.FromResult(Json("{\"id\":5,\"contentRatings\":{\"results\":[{\"iso_3166_1\":\"US\",\"rating\":\"TV-PG\"}]}}"));
            return Task.FromResult(Json("{\"page\":1,\"totalPages\":1,\"results\":[{\"id\":1,\"mediaType\":\"movie\",\"title\":\"Good\",\"adult\":false,\"posterPath\":\"/good\",\"mediaInfo\":{\"status\":5},\"secret\":\"LEAK\"},{\"id\":2,\"mediaType\":\"movie\",\"title\":\"Adult\",\"adult\":true},{\"id\":3,\"mediaType\":\"movie\",\"title\":\"Unknown adult\"},{\"id\":4,\"mediaType\":\"movie\",\"title\":\"Blocked\",\"adult\":false,\"mediaInfo\":{\"status\":6}},{\"id\":5,\"mediaType\":\"tv\",\"name\":\"Show\",\"adult\":false}]}"));
        }));
        var flags = new PluginConfiguration { HomeEnabled = true, DiscoverRowEnabled = true, DiscoverMoviesRowEnabled = true, DiscoverTvRowEnabled = true };
        var controller = Controller(new SeerrClient(http, Options()), flags);
        var trending = Assert.IsType<SourceResult<HomeDiscoverItem>>(Assert.IsType<OkObjectResult>((await controller.GetDiscover(1)).Result).Value);
        Assert.Equal(new[] { "Good", "Show" }, trending.Items.Select(i => i.Title));
        var movies = Assert.IsType<SourceResult<HomeDiscoverItem>>(Assert.IsType<OkObjectResult>((await controller.GetDiscoverMovies(1)).Result).Value);
        Assert.Equal("Good", Assert.Single(movies.Items).Title);
        var tv = Assert.IsType<SourceResult<HomeDiscoverItem>>(Assert.IsType<OkObjectResult>((await controller.GetDiscoverTv(1)).Result).Value);
        Assert.Equal("Show", Assert.Single(tv.Items).Title);
        Assert.Contains(paths, p => p.EndsWith("/discover/trending?page=1"));
        Assert.Contains(paths, p => p.EndsWith("/discover/movies?page=1"));
        Assert.Contains(paths, p => p.EndsWith("/discover/tv?page=1"));
        var wire = JsonSerializer.Serialize(trending);
        Assert.DoesNotContain("LEAK", wire);
        Assert.DoesNotContain("Status", wire);
        Assert.DoesNotContain("Playable", wire);
    }

    [Fact]
    public async Task PinnedSeerrTvShapeNeedsVerifiedSafeRatingAndNeverTrustsAdultField()
    {
        // Seerr v3.4.1 mapTvResult (server/models/Search.ts) omits adult and contentRatings.
        // GET /tv/:id maps content_ratings to contentRatings (server/models/Tv.ts).
        var details = new Dictionary<int, string> {
            [10] = "{\"id\":10,\"contentRatings\":{\"results\":[{\"iso_3166_1\":\"US\",\"rating\":\"TV-G\"}]}}",
            [11] = "{\"id\":11,\"contentRatings\":{\"results\":[{\"iso_3166_1\":\"US\",\"rating\":\"TV-MA\"}]}}",
            [12] = "{\"id\":12,\"contentRatings\":{\"results\":[]}}",
            [13] = "{\"id\":13,\"contentRatings\":{\"results\":[{\"iso_3166_1\":\"US\",\"rating\":\"TV-G\"}]}}",
            [14] = "{\"id\":14,\"contentRatings\":{\"results\":[{\"iso_3166_1\":\"US\",\"rating\":\"TV-14\"}]}}",
            [15] = "{\"id\":999,\"contentRatings\":{\"results\":[{\"iso_3166_1\":\"US\",\"rating\":\"TV-G\"}]}}",
        };
        var detailCalls = new List<int>();
        using var http = new HttpClient(new Handler((request, _) => {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/user/jellyfin/")) return Task.FromResult(Json("{\"id\":42}"));
            Assert.Equal("42", request.Headers.GetValues("X-API-User").Single());
            if (path.Contains("/api/v1/tv/")) {
                var id = int.Parse(path.Split('/').Last());
                detailCalls.Add(id);
                return Task.FromResult(Json(details[id]));
            }
            return Task.FromResult(Json("""
                {"page":1,"totalPages":1,"results":[
                  {"id":10,"mediaType":"tv","name":"Family Show","firstAirDate":"2024-01-01","posterPath":"/family.jpg","genreIds":[],"originCountry":["US"],"originalName":"Family Show","overview":"Family","popularity":1,"voteAverage":7,"voteCount":3},
                  {"id":11,"mediaType":"tv","name":"Mature Show","adult":false},
                  {"id":12,"mediaType":"tv","name":"Unrated Show"},
                  {"id":13,"mediaType":"tv","name":"Explicit Adult","adult":true},
                  {"id":14,"mediaType":"tv","name":"Teen Show"},
                  {"id":15,"mediaType":"tv","name":"Wrong Detail"},
                  {"id":16,"mediaType":"movie","title":"Unrated Movie"},
                  {"id":17,"mediaType":"movie","title":"Safe Movie","adult":false},
                  {"id":18,"mediaType":"tv","name":"Blocklisted Show","mediaInfo":{"status":6}}]}
                """));
        }));
        var flags = new PluginConfiguration { HomeEnabled = true, DiscoverTvRowEnabled = true, DiscoverRowEnabled = true };
        var controller = Controller(new SeerrClient(http, Options()), flags);
        var tv = Assert.IsType<SourceResult<HomeDiscoverItem>>(Assert.IsType<OkObjectResult>((await controller.GetDiscoverTv(1)).Result).Value);
        Assert.Equal("Family Show", Assert.Single(tv.Items).Title);
        var trending = Assert.IsType<SourceResult<HomeDiscoverItem>>(Assert.IsType<OkObjectResult>((await controller.GetDiscover(1)).Result).Value);
        Assert.Equal(new[] { "Family Show", "Safe Movie" }, trending.Items.Select(i => i.Title));
        Assert.DoesNotContain(13, detailCalls);
        Assert.DoesNotContain(16, detailCalls);
        Assert.DoesNotContain(18, detailCalls);
    }

    [Fact]
    public async Task PaginationStopsAtThirdPageAndNeverLoopsOnEmptyOrFailedResults()
    {
        var pages = new List<string>();
        using var http = new HttpClient(new Handler((request, _) => {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("/user/jellyfin/")) return Task.FromResult(Json("{\"id\":42}"));
            pages.Add(path);
            return Task.FromResult(Json("{\"page\":1,\"totalPages\":100,\"results\":[{\"id\":1,\"mediaType\":\"movie\",\"title\":\"Adult\",\"adult\":true}]}"));
        }));
        var flags = new PluginConfiguration { HomeEnabled = true, DiscoverMoviesRowEnabled = true };
        var controller = Controller(new SeerrClient(http, Options()), flags);
        Assert.IsType<BadRequestResult>((await controller.GetDiscoverMovies(0)).Result);
        Assert.IsType<BadRequestResult>((await controller.GetDiscoverMovies(101)).Result);
        var result = Assert.IsType<SourceResult<HomeDiscoverItem>>(Assert.IsType<OkObjectResult>((await controller.GetDiscoverMovies(2)).Result).Value);
        Assert.Empty(result.Items);
        Assert.Equal(3, pages.Count);
        Assert.Equal(new[] { 2, 3, 4 }, pages.Select(p => int.Parse(p.Split("page=")[1])));
    }

    [Fact]
    public async Task TvRatingReadsAreBoundedAndFailedDetailsNeverBecomeCandidates()
    {
        var detailCalls = 0;
        var catalogCalls = 0;
        using var http = new HttpClient(new Handler((request, _) => {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/user/jellyfin/")) return Task.FromResult(Json("{\"id\":42}"));
            if (path.Contains("/api/v1/tv/")) {
                detailCalls++;
                return Task.FromResult(Json("private upstream failure", HttpStatusCode.ServiceUnavailable));
            }
            catalogCalls++;
            var entries = string.Join(',', Enumerable.Range(1, 25).Select(id => $"{{\"id\":{id},\"mediaType\":\"tv\",\"name\":\"Show {id}\"}}"));
            return Task.FromResult(Json($"{{\"page\":1,\"totalPages\":100,\"results\":[{entries}]}}"));
        }));
        var flags = new PluginConfiguration { HomeEnabled = true, DiscoverTvRowEnabled = true };
        var response = Assert.IsType<SourceResult<HomeDiscoverItem>>(Assert.IsType<OkObjectResult>((await Controller(new SeerrClient(http, Options()), flags).GetDiscoverTv(1)).Result).Value);
        Assert.Empty(response.Items);
        Assert.Equal(20, detailCalls);
        Assert.Equal(3, catalogCalls);
    }


    [Fact]
    public async Task MappedFailureAndDeadlineReturnSanitizedError()
    {
        using var failed = new HttpClient(new Handler((_, _) => Task.FromResult(Json("private-error", HttpStatusCode.NotFound))));
        var flags = new PluginConfiguration { HomeEnabled = true, DiscoverRowEnabled = true };
        var result = Assert.IsType<SourceResult<HomeDiscoverItem>>(Assert.IsType<OkObjectResult>((await Controller(new SeerrClient(failed, Options()), flags).GetDiscover(1)).Result).Value);
        Assert.Equal("UserNotMapped", result.Error);
        using var stalled = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json("{}"); }));
        var slow = Controller(new SeerrClient(stalled, Options(), TimeSpan.FromMilliseconds(40)), flags);
        var timeout = Assert.IsType<SourceResult<HomeDiscoverItem>>(Assert.IsType<OkObjectResult>((await slow.GetDiscover(1)).Result).Value);
        Assert.Equal("UpstreamUnavailable", timeout.Error);
    }
}
