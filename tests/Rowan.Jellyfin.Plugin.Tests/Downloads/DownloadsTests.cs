using System;
using System.Collections.Concurrent;
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
using Rowan.Jellyfin.Plugin.Downloads;
using Rowan.Jellyfin.Plugin.Calendar;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Downloads;

public sealed class DownloadsTests
{
    private static readonly Guid Alice = Guid.NewGuid(), Bob = Guid.NewGuid();
    private const string Movie = "{\"page\":1,\"pageSize\":50,\"totalRecords\":1,\"records\":[{\"movieId\":12,\"movie\":{\"id\":12,\"tmdbId\":123,\"title\":\"Film\"},\"status\":\"downloading\",\"size\":100,\"sizeleft\":25,\"title\":\"release-secret\",\"outputPath\":\"/secret\",\"downloadId\":\"secret\",\"statusMessages\":[\"secret\"]}]}";
    private const string Show = "{\"page\":1,\"pageSize\":50,\"totalRecords\":1,\"records\":[{\"seriesId\":34,\"series\":{\"id\":34,\"tvdbId\":456,\"title\":\"Show\"},\"status\":\"queued\",\"size\":100,\"sizeleft\":100,\"downloadClient\":\"secret\"}]}";

    [Fact]
    public async Task TwoExistingUsersSeeSameInstanceWideTitlesWithoutPersonalAttribution()
    {
        var requests = new ConcurrentBag<(string Path, string? Key)>();
        using var http = Stub(request => { requests.Add((request.RequestUri!.PathAndQuery, request.Headers.GetValues("X-Api-Key").Single())); return Json(request.RequestUri.AbsolutePath.Contains("radarr") ? Movie : Show); });
        var client = new ArrDownloadsClient(http, Options(), new DownloadsAdmission());
        foreach (var user in new[] { Alice, Bob })
        {
            var result = Assert.IsType<OkObjectResult>((await Controller(client, user).GetDownloads(CancellationToken.None)).Result);
            var text = JsonSerializer.Serialize(result.Value);
            Assert.Contains("Film", text);
            Assert.Contains("Show", text);
            Assert.Contains("0.75", text);
            foreach (var forbidden in new[] { "secret", "release", "downloadId", "downloadClient", "requestedBy", "userId", "path", "statusMessages" }) Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(4, requests.Count);
        Assert.All(requests, request => Assert.Contains("/api/v3/queue?page=1&pageSize=50&include", request.Path));
        Assert.Contains(requests, r => r.Path.Contains("includeMovie=true") && r.Key == "radarr-key");
        Assert.Contains(requests, r => r.Path.Contains("includeSeries=true") && r.Key == "sonarr-key");
    }

    [Fact]
    public async Task AnonymousUserlessDuplicateAndUnknownClaimsNeverStartWork()
    {
        var calls = 0;
        using var http = Stub(_ => { Interlocked.Increment(ref calls); return Json(Movie); });
        var client = new ArrDownloadsClient(http, Options(), new DownloadsAdmission());
        Assert.IsType<UnauthorizedResult>((await Controller(client, null, authenticated: false).GetDownloads(default)).Result);
        Assert.IsType<ForbidResult>((await Controller(client, null).GetDownloads(default)).Result);
        Assert.IsType<ForbidResult>((await Controller(client, Alice, exists: false).GetDownloads(default)).Result);
        Assert.IsType<ForbidResult>((await Controller(client, Alice, duplicate: true).GetDownloads(default)).Result);
        Assert.Equal(0, calls);
        Assert.NotNull(typeof(DownloadsController).GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal("Downloads", typeof(DownloadsController).GetMethod("GetDownloads")!.GetCustomAttribute<HttpGetAttribute>()?.Template);
    }

    [Fact]
    public async Task DisabledAndInvalidSettingsDoNotSendRequests()
    {
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json(Movie); });
        Assert.Null(ArrDownloadsOptions.FromConfiguration(new PluginConfiguration { RadarrBaseUrl = "https://host", RadarrApiKey = "key" }));
        var config = Config();
        config.RadarrBaseUrl = "https://name:password@host/";
        Assert.Throws<ArgumentException>(() => ArrDownloadsOptions.FromConfiguration(config));
        config.RadarrBaseUrl = "https://host/?key=secret";
        Assert.Throws<ArgumentException>(() => ArrDownloadsOptions.FromConfiguration(config));
        config.RadarrBaseUrl = "https://host/#secret";
        Assert.Throws<ArgumentException>(() => ArrDownloadsOptions.FromConfiguration(config));
        var result = await Controller(new ArrDownloadsClient(http, null, new DownloadsAdmission()), Alice).GetDownloads(default);
        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExcessiveEndpointUrlOrKeyIsRejectedForBothModulesWithoutExposingValues(bool overlongUrl)
    {
        var config = Config();
        config.CalendarEnabled = true;
        config.RadarrBaseUrl = overlongUrl ? "https://arr.example/" + new string('x', 2048) : config.RadarrBaseUrl;
        config.RadarrApiKey = overlongUrl ? config.RadarrApiKey : new string('k', 257);
        var secret = overlongUrl ? new string('x', 2048) : new string('k', 257);
        var error = Assert.Throws<ArgumentException>(() => ArrDownloadsOptions.FromConfiguration(config));
        Assert.DoesNotContain(secret, error.ToString());
        var downloads = Assert.IsType<ArrDownloadsOptions>(ArrDownloadsOptions.TryFromConfiguration(config));
        Assert.True(downloads.RadarrInvalid);
        Assert.NotNull(downloads.Sonarr);
        var calendar = Assert.IsType<ArrCalendarOptions>(ArrCalendarOptions.TryFromConfiguration(config));
        var calls = new ConcurrentBag<string>();
        using var http = Stub(request => { calls.Add(request.RequestUri!.AbsolutePath); return Json("[]"); });
        var result = (await new ArrCalendarClient(http, calendar, new CalendarAdmission()).GetAsync(Alice, new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), default))!;
        Assert.Equal("InvalidConfiguration", result.Radarr.Error);
        Assert.Single(calls);
        Assert.Contains("sonarr", calls.Single());
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result));
    }

    [Fact]
    public void OversizedBlankCredentialsAreInvalidRatherThanSilentlyDisabled()
    {
        var config = Config();
        config.RadarrBaseUrl = new string(' ', 2049);
        config.RadarrApiKey = new string(' ', 257);
        Assert.Throws<ArgumentException>(() => ArrDownloadsOptions.FromConfiguration(config));
        Assert.True(ArrDownloadsOptions.TryFromConfiguration(config)!.RadarrInvalid);
        config.CalendarEnabled = true;
        Assert.True(ArrDownloadsOptions.TryForCalendar(config)!.RadarrInvalid);
    }

    [Fact]
    public void ExactEndpointLengthLimitsRemainUsable()
    {
        var config = Config();
        config.RadarrBaseUrl = "https://arr.example/" + new string('x', 2048 - "https://arr.example/".Length);
        config.RadarrApiKey = new string('k', 256);
        var options = Assert.IsType<ArrDownloadsOptions>(ArrDownloadsOptions.FromConfiguration(config));
        Assert.False(options.RadarrInvalid);
        Assert.NotNull(options.Radarr);
    }

    [Fact]
    public async Task RedirectMalformedJsonAndOversizedResponsesFailClosedWithoutSecrets()
    {
        foreach (var response in new Func<HttpResponseMessage>[] { () => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://attacker.example/") } }, () => Json("{not-json"), () => Json(new string('x', 300_000)) })
        {
            using var http = Stub(_ => response());
            var result = Assert.IsType<OkObjectResult>((await Controller(new ArrDownloadsClient(http, Options(), new DownloadsAdmission()), Alice).GetDownloads(default)).Result);
            var text = JsonSerializer.Serialize(result.Value);
            Assert.DoesNotContain("attacker", text);
            Assert.DoesNotContain("xxx", text);
            Assert.DoesNotContain("Film", text);
        }
    }

    [Fact]
    public async Task ConflictingIdsAndMalformedQueueEntriesFailClosed()
    {
        foreach (var body in new[] { Movie.Replace("\"movieId\":12", "\"movieId\":13"), Movie.Replace("\"tmdbId\":123", "\"tmdbId\":0"), Movie.Replace("\"totalRecords\":1", "\"totalRecords\":2").Replace("}]}", "},{\"movieId\":99,\"movie\":{\"id\":99,\"tmdbId\":123,\"title\":\"Other Film\"}}]}"), Movie.Replace("\"totalRecords\":1", "\"totalRecords\":2").Replace("}]}", "},{\"movieId\":12,\"movie\":{\"id\":12,\"tmdbId\":999,\"title\":\"Other Film\"}}]}") })
        {
            using var http = Stub(r => Json(r.RequestUri!.AbsolutePath.Contains("radarr") ? body : Show));
            var result = Assert.IsType<OkObjectResult>((await Controller(new ArrDownloadsClient(http, Options(), new DownloadsAdmission()), Alice).GetDownloads(default)).Result);
            var text = JsonSerializer.Serialize(result.Value);
            Assert.DoesNotContain("Film", text);
            Assert.Contains("Show", text);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatedTitleQueueRecordsAggregateWithoutLosingSource(bool movie)
    {
        var first = movie ? "{\"movieId\":12,\"movie\":{\"id\":12,\"tmdbId\":123,\"title\":\"Film\"}}" : "{\"seriesId\":34,\"series\":{\"id\":34,\"tvdbId\":456,\"title\":\"Show\"}}";
        var body = $"{{\"page\":1,\"pageSize\":50,\"totalRecords\":2,\"records\":[{first[..^1]},\"status\":\"queued\",\"size\":100,\"sizeleft\":100}}, {first[..^1]},\"status\":\"downloading\",\"size\":100,\"sizeleft\":0}}]}}";
        using var http = Stub(r => Json(r.RequestUri!.AbsolutePath.Contains(movie ? "radarr" : "sonarr") ? body : movie ? Show : Movie));
        var result = Assert.IsType<OkObjectResult>((await Controller(new ArrDownloadsClient(http, Options(), new DownloadsAdmission()), Alice).GetDownloads(default)).Result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var source = json.RootElement.GetProperty(movie ? "Radarr" : "Sonarr");
        Assert.False(source.GetProperty("Partial").GetBoolean());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("Error").ValueKind);
        var item = Assert.Single(source.GetProperty("Items").EnumerateArray());
        Assert.Equal(movie ? 123 : 456, item.GetProperty("TitleId").GetInt32());
        Assert.Equal("Downloading", item.GetProperty("State").GetString());
        Assert.Equal(0.5, item.GetProperty("Progress").GetDouble());
    }

    [Fact]
    public async Task InvalidProgressIsNotPublished()
    {
        foreach (var body in new[] { Movie.Replace("\"sizeleft\":25", "\"sizeleft\":101"), Movie.Replace("\"size\":100", "\"size\":0"), Movie.Replace("\"sizeleft\":25", "\"sizeleft\":-1") })
        {
            using var http = Stub(r => Json(r.RequestUri!.AbsolutePath.Contains("radarr") ? body : Show));
            var result = Assert.IsType<OkObjectResult>((await Controller(new ArrDownloadsClient(http, Options(), new DownloadsAdmission()), Alice).GetDownloads(default)).Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            var item = json.RootElement.GetProperty("Radarr").GetProperty("Items")[0];
            Assert.Equal(JsonValueKind.Null, item.GetProperty("Progress").ValueKind);
        }
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndAdmissionRejectsExcessWithoutWork()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var calls = 0;
        using var http = Stub(_ => { calls++; return Json(Movie); });
        var client = new ArrDownloadsClient(http, Options(), new DownloadsAdmission());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Controller(client, Alice).GetDownloads(cancel.Token));
        Assert.Equal(0, calls);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var blocked = new HttpClient(new AsyncHandler(async (_, token) => { Interlocked.Increment(ref calls); await gate.Task.WaitAsync(token); return Json(Movie); }));
        var busy = new ArrDownloadsClient(blocked, Options(), new DownloadsAdmission(globalLimit: 1, perUserLimit: 1));
        var first = Controller(busy, Alice).GetDownloads(default);
        await Task.Yield();
        Assert.IsType<StatusCodeResult>((await Controller(busy, Bob).GetDownloads(default)).Result);
        Assert.IsType<StatusCodeResult>((await Controller(busy, Alice).GetDownloads(default)).Result);
        gate.SetResult();
        await first;
    }

    [Fact]
    public async Task InFlightCancellationReleasesAdmissionForNextCaller()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AsyncHandler(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json(Movie); }));
        var admission = new DownloadsAdmission(globalLimit: 1);
        var client = new ArrDownloadsClient(http, Options(), admission);
        using var cancellation = new CancellationTokenSource();
        var pending = Controller(client, Alice).GetDownloads(cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        using var other = Stub(request => Json(request.RequestUri!.AbsolutePath.Contains("radarr") ? Movie : Show));
        var next = new ArrDownloadsClient(other, Options(), admission);
        Assert.IsType<OkObjectResult>((await Controller(next, Bob).GetDownloads(default)).Result);
    }

    [Fact]
    public async Task InvalidEnabledSourceDoesNotDisableValidSource()
    {
        foreach (var invalidRadarr in new[] { false, true })
        {
            var config = Config();
            if (invalidRadarr) config.RadarrBaseUrl = "https://user:pass@host/";
            else config.SonarrBaseUrl = "https://user:pass@host/";
            var options = Assert.IsType<ArrDownloadsOptions>(ArrDownloadsOptions.TryFromConfiguration(config));
            var calls = new ConcurrentBag<string>();
            using var http = Stub(r => { calls.Add(r.RequestUri!.AbsolutePath); return Json(r.RequestUri.AbsolutePath.Contains("radarr") ? Movie : Show); });
            var result = Assert.IsType<OkObjectResult>((await Controller(new ArrDownloadsClient(http, options, new DownloadsAdmission()), Alice).GetDownloads(default)).Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            var valid = json.RootElement.GetProperty(invalidRadarr ? "Sonarr" : "Radarr");
            var invalid = json.RootElement.GetProperty(invalidRadarr ? "Radarr" : "Sonarr");
            Assert.Single(valid.GetProperty("Items").EnumerateArray());
            Assert.Empty(invalid.GetProperty("Items").EnumerateArray());
            Assert.True(invalid.GetProperty("Partial").GetBoolean());
            Assert.Equal("InvalidConfiguration", invalid.GetProperty("Error").GetString());
            Assert.Single(calls);
            Assert.Contains(invalidRadarr ? "sonarr" : "radarr", calls.Single());
        }
        Assert.Equal("ArrEndpoint [REDACTED]", ArrDownloadsOptions.FromConfiguration(Config())!.Radarr!.ToString());
    }

    [Fact]
    public async Task BoundedPaginationMarksTruncationAndDoesNotRequestFourthPage()
    {
        var pages = new ConcurrentBag<string>();
        using var http = Stub(request =>
        {
            var url = request.RequestUri!.PathAndQuery;
            pages.Add(url);
            if (!url.Contains("radarr")) return Json("{\"page\":1,\"pageSize\":50,\"totalRecords\":0,\"records\":[]}");
            var page = int.Parse(url.Split("page=")[1].Split('&')[0]);
            var records = Enumerable.Range((page - 1) * 50 + 1, 50).Select(id => $"{{\"movieId\":{id},\"movie\":{{\"id\":{id},\"tmdbId\":{id},\"title\":\"Film {id}\"}},\"status\":\"queued\"}}");
            return Json($"{{\"page\":{page},\"pageSize\":50,\"totalRecords\":500,\"records\":[{string.Join(',', records)}]}}");
        });
        var result = Assert.IsType<OkObjectResult>((await Controller(new ArrDownloadsClient(http, Options(), new DownloadsAdmission()), Alice).GetDownloads(default)).Result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.True(json.RootElement.GetProperty("Radarr").GetProperty("Partial").GetBoolean());
        Assert.Equal(100, json.RootElement.GetProperty("Radarr").GetProperty("Items").GetArrayLength());
        Assert.Equal(2, pages.Count(p => p.Contains("radarr")));
    }

    [Fact]
    public async Task MissingOrInconsistentPaginationFailsClosedRatherThanInventingCompleteness()
    {
        foreach (var body in new[] { Movie.Replace("\"pageSize\":50", "\"pageSize\":100"), Movie.Replace("\"totalRecords\":1", "\"totalRecords\":2"), Movie.Replace("\"totalRecords\":1", "\"totalRecords\":0"), Movie.Replace("\"page\":1", "\"page\":2") })
        {
            using var http = Stub(request => Json(request.RequestUri!.AbsolutePath.Contains("radarr") ? body : Show));
            var response = Assert.IsType<OkObjectResult>((await Controller(new ArrDownloadsClient(http, Options(), new DownloadsAdmission()), Alice).GetDownloads(default)).Result);
            Assert.DoesNotContain("Film", JsonSerializer.Serialize(response.Value));
        }
    }

    private static PluginConfiguration Config() => new() { DownloadsEnabled = true, RadarrBaseUrl = "https://arr.example/radarr/", RadarrApiKey = "radarr-key", SonarrBaseUrl = "https://arr.example/sonarr/", SonarrApiKey = "sonarr-key" };
    private static ArrDownloadsOptions? Options() => ArrDownloadsOptions.FromConfiguration(Config());
    private static DownloadsController Controller(ArrDownloadsClient client, Guid? user, bool authenticated = true, bool exists = true, bool duplicate = false)
    {
        var claims = user is { } id ? new[] { new Claim("Jellyfin-UserId", id.ToString()) } : Array.Empty<Claim>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(duplicate ? claims.Concat(claims) : claims, authenticated ? "test" : null));
        return new DownloadsController(client, _ => exists) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } } };
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpClient Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new AsyncHandler((request, _) => Task.FromResult(respond(request))));
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
}
