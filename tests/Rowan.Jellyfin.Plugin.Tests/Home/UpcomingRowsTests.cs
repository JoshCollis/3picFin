using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Calendar;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Home;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class UpcomingRowsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
    private const string Movies = "[{\"id\":1,\"tmdbId\":17,\"title\":\"Future\",\"monitored\":true,\"hasFile\":null,\"movieFileId\":0,\"inCinemas\":\"2026-10-04T00:00:00Z\",\"digitalRelease\":\"2026-10-02T00:00:00Z\",\"path\":\"/hidden\"},{\"id\":2,\"tmdbId\":18,\"title\":\"Present\",\"monitored\":true,\"movieFileId\":123,\"inCinemas\":\"2026-10-03T00:00:00Z\"},{\"id\":3,\"tmdbId\":19,\"title\":\"Unknown\",\"inCinemas\":\"2026-10-03T00:00:00Z\"}]";
    private const string Shows = "[{\"id\":9,\"seriesId\":34,\"seasonNumber\":1,\"episodeNumber\":2,\"title\":\"Pilot\",\"airDateUtc\":\"2026-10-02T13:30:00Z\",\"monitored\":true,\"hasFile\":false,\"series\":{\"id\":34,\"tvdbId\":456,\"title\":\"Show\",\"path\":\"/hidden\"}},{\"id\":10,\"seriesId\":34,\"seasonNumber\":1,\"episodeNumber\":3,\"title\":\"Downloaded\",\"airDateUtc\":\"2026-10-03T13:30:00Z\",\"monitored\":true,\"hasFile\":true,\"series\":{\"id\":34,\"tvdbId\":456,\"title\":\"Show\"}}]";
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    private static PluginConfiguration Config() => new() { HomeEnabled = true, CalendarEnabled = true, UpcomingMoviesRowEnabled = true, UpcomingShowsRowEnabled = true,
        RadarrBaseUrl = "https://arr.example/radarr/", RadarrApiKey = "secret", SonarrBaseUrl = "https://arr.example/sonarr/", SonarrApiKey = "secret" };
    private static UpcomingRowsController Controller(PluginConfiguration config, bool claim = true, bool exists = true, bool authenticated = true, Action? called = null)
    {
        var http = new HttpClient(new Handler(request => { called?.Invoke(); return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("radarr") ? Movies : Shows, Encoding.UTF8, "application/json") }; }));
        var client = new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(config), new CalendarAdmission());
        var claims = claim ? new[] { new Claim("Jellyfin-UserId", Guid.NewGuid().ToString()) } : [];
        return new UpcomingRowsController(client, _ => exists, () => config, () => Now) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "test" : null)) } } };
    }

    [Fact]
    public async Task IndependentlyEnabledRowsUseValidatedSignalsAndOnlyAllowlistedHouseholdCards()
    {
        var controller = Controller(Config());
        var movies = Assert.IsType<OkObjectResult>((await controller.GetRow("UpcomingMovies")).Result);
        var movie = Assert.Single(Assert.IsType<UpcomingHomeRow>(movies.Value).Items);
        Assert.Equal("Future", movie.Title);
        Assert.Equal(17, movie.TitleId);
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T00:00:00Z"), movie.Date);
        var shows = Assert.IsType<OkObjectResult>((await controller.GetRow("UpcomingShows")).Result);
        var show = Assert.Single(Assert.IsType<UpcomingHomeRow>(shows.Value).Items);
        Assert.Equal("Pilot", show.EpisodeTitle);
        Assert.Equal(456, show.TitleId);
        var wire = System.Text.Json.JsonSerializer.Serialize(new { movie, show });
        foreach (var secret in new[] { "/hidden", "secret", "hasFile", "monitored", "path", "playable", "UserId" })
            Assert.DoesNotContain(secret, wire, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void PolicyRejectsUnknownStatusAndPastDatesAndDeduplicatesMovieEvents()
    {
        var date = Now.AddDays(2);
        var events = new[] {
            new CalendarEvent("Radarr", "movie", 1, "Movie", "Cinema", date, null, null, null, true, false),
            new CalendarEvent("Radarr", "movie", 1, "Movie", "Digital", date.AddDays(1), null, null, null, true, false),
            new CalendarEvent("Radarr", "movie", 2, "Unknown", "Cinema", date, null, null, null),
            new CalendarEvent("Radarr", "movie", 3, "Past", "Cinema", Now.AddMinutes(-1), null, null, null, true, false),
            new CalendarEvent("Radarr", "movie", 4, "Unmonitored", "Cinema", date, null, null, null, false, false)
        };
        var card = Assert.Single(UpcomingRowsPolicy.Select(events, "UpcomingMovies", Now));
        Assert.Equal(1, card.TitleId);
        Assert.Equal(date, card.Date);
    }

    [Fact]
    public async Task MissingOrMalformedStatusCannotBecomeUpcomingAndOneSourceFailureIsIsolated()
    {
        var config = Config();
        var missing = Movies.Replace("\"movieFileId\":0,", "");
        var malformed = Movies.Replace("\"monitored\":true", "\"monitored\":\"true\"");
        foreach (var payload in new[] { missing, malformed })
        {
            using var http = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("radarr") ? payload : Shows, Encoding.UTF8, "application/json") }));
            var client = new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(config), new CalendarAdmission());
            var result = await client.GetAsync(Guid.NewGuid(), Now.UtcDateTime.Date, Now.UtcDateTime.Date.AddDays(31), default);
            Assert.NotNull(result);
            Assert.Empty(UpcomingRowsPolicy.Select(result.Radarr.Items, "UpcomingMovies", Now));
            Assert.Single(UpcomingRowsPolicy.Select(result.Sonarr.Items, "UpcomingShows", Now));
            if (payload == malformed) Assert.Equal("UpstreamUnavailable", result.Radarr.Error);
        }
    }

    [Fact]
    public async Task ConflictingDuplicateStatusFailsClosedForItsSource()
    {
        var conflicting = "[" + Movies[1..^1] + "," + Movies[1..^1].Split("},{")[0].Replace("\"movieFileId\":0", "\"movieFileId\":1") + "}]";
        using var http = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("radarr") ? conflicting : Shows, Encoding.UTF8, "application/json") }));
        var result = await new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission())
            .GetAsync(Guid.NewGuid(), Now.UtcDateTime.Date, Now.UtcDateTime.Date.AddDays(31), default);
        Assert.Equal("UpstreamUnavailable", result!.Radarr.Error);
        Assert.Single(UpcomingRowsPolicy.Select(result.Sonarr.Items, "UpcomingShows", Now));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("-1")]
    [InlineData("100000001")]
    [InlineData("1.5")]
    [InlineData("\"0\"")]
    public async Task InvalidMovieFileIdNeverAdmitsUpcoming(string value)
    {
        var payload = Movies.Replace("\"movieFileId\":0", $"\"movieFileId\":{value}");
        using var http = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("radarr") ? payload : Shows, Encoding.UTF8, "application/json") }));
        var result = await new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission())
            .GetAsync(Guid.NewGuid(), Now.UtcDateTime.Date, Now.UtcDateTime.Date.AddDays(31), default);
        Assert.Empty(UpcomingRowsPolicy.Select(result!.Radarr.Items, "UpcomingMovies", Now));
        Assert.Single(UpcomingRowsPolicy.Select(result.Sonarr.Items, "UpcomingShows", Now));
    }

    [Fact]
    public async Task RadarrCompatibilityHasFileDoesNotOverrideMappedMovieFileId()
    {
        var payload = Movies.Replace("\"hasFile\":null", "\"hasFile\":true")
            .Replace("\"movieFileId\":123", "\"hasFile\":false,\"movieFileId\":123");
        using var http = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("radarr") ? payload : Shows, Encoding.UTF8, "application/json") }));
        var result = await new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission())
            .GetAsync(Guid.NewGuid(), Now.UtcDateTime.Date, Now.UtcDateTime.Date.AddDays(31), default);
        Assert.Equal(17, Assert.Single(UpcomingRowsPolicy.Select(result!.Radarr.Items, "UpcomingMovies", Now)).TitleId);
    }

    [Fact]
    public async Task FlagsAndMissingIdentityFailBeforeAnyArrRead()
    {
        var config = Config(); var calls = 0;
        config.UpcomingMoviesRowEnabled = false;
        Assert.IsType<NotFoundResult>((await Controller(config, called: () => calls++).GetRow("UpcomingMovies")).Result);
        Assert.IsType<ForbidResult>((await Controller(config, claim: false, called: () => calls++).GetRow("UpcomingShows")).Result);
        Assert.IsType<ForbidResult>((await Controller(config, exists: false, called: () => calls++).GetRow("UpcomingShows")).Result);
        Assert.IsType<ForbidResult>((await Controller(config, authenticated: false, called: () => calls++).GetRow("UpcomingShows")).Result);
        Assert.IsType<NotFoundResult>((await Controller(config, called: () => calls++).GetRow("Anything")).Result);
        config.CalendarEnabled = false;
        Assert.IsType<NotFoundResult>((await Controller(config, called: () => calls++).GetRow("UpcomingShows")).Result);
        Assert.Equal(0, calls);
    }
}
