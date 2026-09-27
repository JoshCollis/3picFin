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
using Rowan.Jellyfin.Plugin.Calendar;
using Rowan.Jellyfin.Plugin.Configuration;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Calendar;

public sealed class CalendarTests
{
    private const string Movie = "[{\"id\":12,\"tmdbId\":123,\"title\":\"Film\",\"inCinemas\":\"2026-10-01T00:00:00Z\",\"digitalRelease\":\"2026-10-02T12:00:00Z\",\"physicalRelease\":\"2026-10-03T00:00:00Z\",\"path\":\"/secret\",\"hasFile\":true}]";
    private const string Episodes = "[{\"id\":9,\"seriesId\":34,\"seasonNumber\":1,\"episodeNumber\":2,\"title\":\"Pilot\",\"airDateUtc\":\"2026-10-02T13:30:00Z\",\"series\":{\"id\":34,\"tvdbId\":456,\"title\":\"Show\",\"path\":\"/private\"},\"episodeFile\":{\"path\":\"/secret\"}}]";
    private static readonly Guid Alice = Guid.NewGuid(), Bob = Guid.NewGuid();
    private static PluginConfiguration Config() => new() { CalendarEnabled = true, RadarrBaseUrl = "https://arr.example/radarr/", RadarrApiKey = "r-key", SonarrBaseUrl = "https://arr.example/sonarr/", SonarrApiKey = "s-key" };
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpClient Stub(Func<HttpRequestMessage, HttpResponseMessage> callback) => new(new Handler((r, _) => Task.FromResult(callback(r))));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token);
    }
    private static CalendarController Controller(ArrCalendarClient client, Guid? id, bool authenticated = true, bool exists = true, bool duplicate = false)
    {
        var claims = id is { } user ? new[] { new Claim("Jellyfin-UserId", user.ToString()) } : [];
        return new CalendarController(client, _ => exists) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(duplicate ? claims.Concat(claims) : claims, authenticated ? "test" : null)) } } };
    }
    private static Task<ActionResult<CalendarResponse>> Read(ArrCalendarClient client, Guid? id = null) => Controller(client, id ?? Alice).GetCalendar("2026-10-01", "2026-10-04", default);

    [Fact]
    public async Task SharedCalendarExpandsAllMovieDatesAndDeduplicatesUtcEpisodesWithoutSecrets()
    {
        var requests = new List<(string, string)>();
        using var http = Stub(r => { requests.Add((r.RequestUri!.PathAndQuery, r.Headers.GetValues("X-Api-Key").Single())); return Json(r.RequestUri.AbsolutePath.Contains("radarr") ? Movie : Episodes.Replace("}]", "}," + Episodes[1..])); });
        var client = new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission());
        foreach (var user in new[] { Alice, Bob })
        {
            var result = Assert.IsType<OkObjectResult>((await Controller(client, user).GetCalendar("2026-10-01", "2026-10-04", default)).Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal(3, json.RootElement.GetProperty("Radarr").GetProperty("Items").GetArrayLength());
            Assert.Single(json.RootElement.GetProperty("Sonarr").GetProperty("Items").EnumerateArray());
            var output = json.RootElement.ToString();
            Assert.Contains("Film", output); Assert.Contains("Pilot", output);
            foreach (var secret in new[] { "/secret", "/private", "hasFile", "episodeFile", "requestedBy", "r-key", "s-key" }) Assert.DoesNotContain(secret, output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("private, no-store", ControllerCache(client, user));
        }
        Assert.Contains(requests, x => x.Item1 == "/radarr/api/v3/calendar?start=2026-10-01&end=2026-10-04&unmonitored=false" && x.Item2 == "r-key");
        Assert.Contains(requests, x => x.Item1 == "/sonarr/api/v3/calendar?start=2026-10-01&end=2026-10-04&unmonitored=false&includeSeries=true&includeEpisodeFile=false&includeEpisodeImages=false" && x.Item2 == "s-key");
    }
    private static string ControllerCache(ArrCalendarClient client, Guid user)
    {
        // The main response is checked separately; no public caching of shared titles.
        var controller = Controller(client, user);
        controller.GetCalendar("2026-10-01", "2026-10-04", default).GetAwaiter().GetResult();
        return controller.Response.Headers.CacheControl.ToString();
    }

    [Fact]
    public async Task AuthenticationAndDateBoundsRejectBeforeUpstream()
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json(Movie); });
        var client = new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission());
        Assert.IsType<UnauthorizedResult>((await Controller(client, null, authenticated: false).GetCalendar("2026-10-01", "2026-10-02", default)).Result);
        Assert.IsType<ForbidResult>((await Controller(client, null).GetCalendar("2026-10-01", "2026-10-02", default)).Result);
        Assert.IsType<ForbidResult>((await Controller(client, Alice, exists: false).GetCalendar("2026-10-01", "2026-10-02", default)).Result);
        Assert.IsType<ForbidResult>((await Controller(client, Alice, duplicate: true).GetCalendar("2026-10-01", "2026-10-02", default)).Result);
        foreach (var (start, end) in new[] { ("bad", "2026-10-02"), ("2026-10-02", "2026-10-02"), ("2026-10-01", "2026-12-01"), ("2026-10-01?key=evil", "2026-10-02") })
            Assert.IsType<BadRequestObjectResult>((await Controller(client, Alice).GetCalendar(start, end, default)).Result);
        Assert.Equal(0, calls);
        Assert.NotNull(typeof(CalendarController).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public async Task IndependentDefaultOffAndPerSourceInvalidConfiguration()
    {
        var config = Config(); config.CalendarEnabled = false; config.DownloadsEnabled = true;
        Assert.Null(ArrCalendarOptions.TryFromConfiguration(config));
        config.CalendarEnabled = true; config.RadarrBaseUrl = "https://user:pass@host/";
        var calls = 0;
        using var http = Stub(r => { calls++; return Json(Episodes); });
        var response = Assert.IsType<OkObjectResult>((await Read(new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(config), new CalendarAdmission()))).Result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
        Assert.Equal("InvalidConfiguration", json.RootElement.GetProperty("Radarr").GetProperty("Error").GetString());
        Assert.Single(json.RootElement.GetProperty("Sonarr").GetProperty("Items").EnumerateArray());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BadPayloadRedirectAndOversizedResponseDoNotLeakOrSuppressHealthySource()
    {
        foreach (var bad in new Func<HttpResponseMessage>[] { () => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://attacker.example") } }, () => Json("{bad"), () => Json(new string('x', 140_000)), () => Json("[{}]") })
        {
            using var http = Stub(r => r.RequestUri!.AbsolutePath.Contains("radarr") ? bad() : Json(Episodes));
            var result = Assert.IsType<OkObjectResult>((await Read(new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission()))).Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal("UpstreamUnavailable", json.RootElement.GetProperty("Radarr").GetProperty("Error").GetString());
            Assert.Empty(json.RootElement.GetProperty("Radarr").GetProperty("Items").EnumerateArray());
            Assert.Single(json.RootElement.GetProperty("Sonarr").GetProperty("Items").EnumerateArray());
            Assert.DoesNotContain("attacker", json.RootElement.ToString());
        }
    }

    [Fact]
    public async Task ConflictingMovieIdentityFailsOnlyItsSource()
    {
        var conflict = "[" + Movie[1..^1] + "," + Movie[1..^1].Replace("\"tmdbId\":123", "\"tmdbId\":999") + "]";
        using var http = Stub(r => Json(r.RequestUri!.AbsolutePath.Contains("radarr") ? conflict : Episodes));
        var result = Assert.IsType<OkObjectResult>((await Read(new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission()))).Result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal("UpstreamUnavailable", json.RootElement.GetProperty("Radarr").GetProperty("Error").GetString());
        Assert.Single(json.RootElement.GetProperty("Sonarr").GetProperty("Items").EnumerateArray());
    }

    [Fact]
    public async Task UtcHalfOpenWindowFiltersAndCancellationReleasesAdmission()
    {
        var episodes = "[" + Episodes[1..^1] + "," + Episodes[1..^1].Replace("2026-10-02T13:30:00Z", "2026-10-04T00:00:00Z").Replace("\"id\":9", "\"id\":10") + "]";
        using var http = Stub(r => Json(r.RequestUri!.AbsolutePath.Contains("radarr") ? Movie : episodes));
        var response = Assert.IsType<OkObjectResult>((await Read(new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission()))).Result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
        Assert.Single(json.RootElement.GetProperty("Sonarr").GetProperty("Items").EnumerateArray());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Controller(new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission()), Alice).GetCalendar("2026-10-01", "2026-10-04", canceled.Token));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var blocked = new HttpClient(new Handler(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json(Movie); }));
        var admission = new CalendarAdmission(1, 1);
        var client = new ArrCalendarClient(blocked, ArrCalendarOptions.TryFromConfiguration(Config()), admission);
        using var source = new CancellationTokenSource();
        var pending = Controller(client, Alice).GetCalendar("2026-10-01", "2026-10-04", source.Token);
        await entered.Task;
        Assert.IsType<StatusCodeResult>((await Controller(client, Bob).GetCalendar("2026-10-01", "2026-10-04", default)).Result);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.IsType<OkObjectResult>((await Read(new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), admission))).Result);
    }

    [Fact]
    public async Task StalledRadarrDoesNotPreventSonarrFromStartingWithinItsOwnBudget()
    {
        var radarrEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sonarrEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRadarr = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("radarr"))
            {
                radarrEntered.TrySetResult();
                await releaseRadarr.Task.WaitAsync(token);
                return Json(Movie);
            }
            sonarrEntered.TrySetResult();
            return Json(Episodes);
        }));
        using var caller = new CancellationTokenSource();
        var pending = Controller(new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission()), Alice)
            .GetCalendar("2026-10-01", "2026-10-04", caller.Token);
        try
        {
            await radarrEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await sonarrEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            releaseRadarr.TrySetResult();
            var response = Assert.IsType<OkObjectResult>((await pending).Result);
            var calendar = Assert.IsType<CalendarResponse>(response.Value);
            Assert.Single(calendar.Sonarr.Items);
            Assert.Equal(3, calendar.Radarr.Items.Count);
        }
        finally
        {
            caller.Cancel();
            releaseRadarr.TrySetResult();
            try { await pending; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task TooManyRecordsReturnBoundedHonestPartialAfterLocalValidationAndFiltering()
    {
        var outside = Movie[1..^1].Replace("2026-10-01T00:00:00Z", "2026-09-01T00:00:00Z").Replace("2026-10-02T12:00:00Z", "2026-09-02T12:00:00Z").Replace("2026-10-03T00:00:00Z", "2026-09-03T00:00:00Z");
        var records = Enumerable.Range(1, 101).Select(id => (id == 1 ? outside : Movie[1..^1]).Replace("\"id\":12", $"\"id\":{id}").Replace("\"tmdbId\":123", $"\"tmdbId\":{id}"));
        using var http = Stub(request => Json(request.RequestUri!.AbsolutePath.Contains("radarr") ? $"[{string.Join(',', records)}]" : Episodes));
        var response = Assert.IsType<OkObjectResult>((await Read(new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission()))).Result);
        var calendar = Assert.IsType<CalendarResponse>(response.Value);
        Assert.True(calendar.Radarr.Partial);
        Assert.Null(calendar.Radarr.Error);
        Assert.Equal(99 * 3, calendar.Radarr.Items.Count);
        Assert.DoesNotContain(calendar.Radarr.Items, item => item.TitleId == 101);
        Assert.Single(calendar.Sonarr.Items);
        Assert.False(calendar.Sonarr.Partial);
    }

    [Fact]
    public async Task RadarrTimeoutKeepsSonarrResultRatherThanSpendingItsBudget()
    {
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("sonarr")) return Json(Episodes);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(Movie);
        }));
        var response = Assert.IsType<OkObjectResult>((await Read(new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission()))).Result);
        var calendar = Assert.IsType<CalendarResponse>(response.Value);
        Assert.Equal("UpstreamUnavailable", calendar.Radarr.Error);
        Assert.Single(calendar.Sonarr.Items);
        Assert.Null(calendar.Sonarr.Error);
    }

    [Fact]
    public async Task SonarrExcessRecordsAlsoReturnBoundedPartial()
    {
        var episodes = Enumerable.Range(1, 101).Select(id => Episodes[1..^1].Replace("\"id\":9", $"\"id\":{id}"));
        using var http = Stub(request => Json(request.RequestUri!.AbsolutePath.Contains("sonarr") ? $"[{string.Join(',', episodes)}]" : Movie));
        var response = Assert.IsType<OkObjectResult>((await Read(new ArrCalendarClient(http, ArrCalendarOptions.TryFromConfiguration(Config()), new CalendarAdmission()))).Result);
        var calendar = Assert.IsType<CalendarResponse>(response.Value);
        Assert.True(calendar.Sonarr.Partial);
        Assert.Null(calendar.Sonarr.Error);
        Assert.Equal(100, calendar.Sonarr.Items.Count);
        Assert.Equal(3, calendar.Radarr.Items.Count);
    }
}
