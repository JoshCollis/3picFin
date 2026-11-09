using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Rowan.Jellyfin.Plugin.Assistant;
using Rowan.Jellyfin.Plugin.Discovery;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Assistant;

public class IntelligenceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
    private static SeerrClient Seerr(HttpClient http) => new(http, SeerrOptions.FromConfiguration(new() { SeerrEnabled = true, SeerrBaseUrl = "http://seerr.test", SeerrApiKey = "fixture-key" }));

    [Fact]
    public void LibraryQuerySupportsPeopleYearsFavoritesAndUsefulSortsWithoutHomeLimits()
    {
        var user = new User("test", "test", "test"); var roots = new[] { Guid.NewGuid() };
        var query = AiLibrary.SearchQuery(user, roots, "", "Drama", "movie", false, 40, 90, "recent", "Amy Adams", 2016, true);
        Assert.Equal("Amy Adams", query.Person); Assert.Equal(new[] { 2016 }, query.Years); Assert.True(query.IsFavorite);
        Assert.Equal(ItemSortBy.DateCreated, query.OrderBy[0].OrderBy); Assert.Equal(780, query.StartIndex); Assert.Equal(roots, query.AncestorIds);
        Assert.Equal(ItemSortBy.CommunityRating, AiLibrary.SearchQuery(user, roots, "", "", "all", null, 1, sort: "rating").OrderBy[0].OrderBy);
        var episodes = AiLibrary.EpisodeQuery(user, roots[0], 2, 3);
        Assert.Same(user, episodes.User); Assert.Equal(roots, episodes.AncestorIds); Assert.Equal(2, episodes.ParentIndexNumber);
        Assert.False(episodes.IsVirtualItem); Assert.Equal(40, episodes.StartIndex); Assert.Equal(21, episodes.Limit);
        Assert.Equal(new[] { BaseItemKind.Episode }, episodes.IncludeItemTypes);
    }

    [Fact]
    public void HistoryKeepsOriginalIntentRecentTurnsAndCompactCardIdentitiesWithinBudget()
    {
        var thread = new AiThread(); thread.Messages.Add(new() { Role = "user", Text = "No horror, local library only" });
        for (var i = 0; i < 30; i++) thread.Messages.Add(new() { Text = new string('x', 4000), Cards = [new("movie:1", "One", "movie", Overview: new string('y', 10000))] });
        thread.Messages.Add(new() { Role = "user", Text = "Something shorter, please" });
        var history = AiJson.Write(AiHistory.Replay(thread));
        Assert.Contains("No horror, local library only", history); Assert.Contains("Something shorter, please", history);
        Assert.Contains("movie:1", history); Assert.DoesNotContain(new string('y', 100), history); Assert.True(history.Length < 36_000);
    }

    [Fact]
    public async Task RelatedDiscoveryIsMappedReadOnlyTypedAndNeverReturnsRawUpstreamFields()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/")) return Json("{\"id\":7,\"permissions\":32}");
            Assert.Equal("7", request.Headers.GetValues("X-API-User").Single());
            Assert.Equal("/api/v1/movie/9/recommendations", request.RequestUri.AbsolutePath);
            return Json("""{"page":1,"totalPages":1,"secret":"private","results":[{"id":10,"title":"Fresh suggestion","adult":false},{"id":11,"title":"Blocked","mediaInfo":{"status":6}},{"id":12,"title":"Adult","adult":true}]}""");
        }));
        var tools = new AiTools(null!, Seerr(http), null!, null!, (_, _, _) => Task.FromResult<AiCard?>(new("movie:9", "Seed", "movie", 9)));
        var result = AiJson.Write(await tools.Run(new(UserId, new(), new(), () => true), "find_related_titles", JsonSerializer.SerializeToElement(new { key = "movie:9" }), default));
        Assert.Contains("Fresh suggestion", result); Assert.DoesNotContain("private", result); Assert.DoesNotContain("Blocked", result); Assert.DoesNotContain("Adult", result);
        Assert.Contains("\"complete\":true", result);
        await Assert.ThrowsAsync<ArgumentException>(() => Seerr(http).GetRelatedTitlesAsync(UserId, "movie", 1, "../secret", 1, default));
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public async Task OwnRequestsCannotExposeOtherUsersEvenWhenUpstreamReturnsThem(int owner, bool success)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/")) return Json("{\"id\":7}");
            Assert.Contains("requestedBy=7", request.RequestUri.Query); Assert.Contains("skip=20", request.RequestUri.Query);
            return Json("{\"pageInfo\":{\"page\":2,\"pages\":2},\"results\":[{\"id\":42,\"status\":2,\"type\":\"movie\",\"media\":{\"tmdbId\":9},\"requestedBy\":{\"id\":" + owner + ",\"email\":\"private@test\"}}]}");
        }));
        var seerr = Seerr(http);
        var result = await seerr.GetOwnRequestsAsync(UserId, 2, default);
        Assert.Equal(success, result.IsSuccess);
        var output = AiJson.Write(await new AiTools(null!, seerr, null!, null!).Run(new(UserId, new(), new(), () => true), "get_my_requests", JsonSerializer.SerializeToElement(new { page = 2 }), default));
        Assert.DoesNotContain("private@test", output);
        if (!success) Assert.DoesNotContain("movie:9", output);
    }

    [Fact]
    public async Task TitleDetailsCarryUsefulMetadataAndFreshEligibilityFromOneMappedRead()
    {
        var reads = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            reads++;
            return Json(request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/") ? "{\"id\":7,\"permissions\":32}" :
                """{"id":9,"title":"Fixture","runtime":116,"genres":[{"name":"Science Fiction"}],"voteAverage":8.2}""");
        }));
        var result = await Seerr(http).GetTitleDetailAsync(UserId, "movie", 9, default);
        Assert.Equal(116, result.Value!.RuntimeMinutes); Assert.Equal("Science Fiction", Assert.Single(result.Value.Genres));
        Assert.Equal(8.2, result.Value.CommunityRating); Assert.True(result.RequestOptions!.Value!.CanRequest); Assert.Equal(2, reads);
    }

    [Fact]
    public async Task DisabledPersonalizationAndIntegrationsAreNotAdvertisedOrRead()
    {
        var tools = new AiTools(null!, new SeerrClient(new HttpClient(), null), null!, null!);
        var definitions = AiJson.Write(tools.AvailableDefinitions(UserId, new() { Personalization = false, LearnMemory = false }));
        Assert.Contains("search_library", definitions);
        foreach (var name in new[] { "browse_catalog", "search_live_tv_guide", "remember_taste", "get_viewing_context", "get_my_requests" }) Assert.DoesNotContain(name, definitions);
        var output = await tools.Run(new(UserId, new() { Personalization = false }, new(), () => true), "get_viewing_context", JsonSerializer.SerializeToElement(new { }), default);
        Assert.Contains("disabled", AiJson.Write(output));
    }
}
