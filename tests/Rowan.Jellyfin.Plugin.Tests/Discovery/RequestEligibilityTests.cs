using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Discovery;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Discovery;

public sealed class RequestEligibilityTests
{
    private static readonly Guid User = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static string Detail(int status = 1, int status4k = 1, string requests = "[]", string seasons = "[]") =>
        $$$"""{"id":9,"title":"Avengers: Doomsday","name":"Synthetic series","seasons":[{"seasonNumber":1},{"seasonNumber":2},{"seasonNumber":3},{"seasonNumber":4}],"mediaInfo":{"status":{{{status}}},"status4k":{{{status4k}}},"requests":{{{requests}}},"seasons":{{{seasons}}}}}""";

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, true)]
    [InlineData(0, false)]
    [InlineData(99, false)]
    public async Task MovieDetailsOptionsAndDirectSubmissionAgree(int status, bool allowed)
    {
        using var fixture = new Fixture(() => Detail(status));
        var details = await fixture.Client.GetTitleDetailAsync(User, "movie", 9, default);
        var options = await fixture.Client.GetRequestOptionsAsync(User, "movie", 9, default);
        Assert.Equal(allowed, details.Value!.CanRequest);
        Assert.Equal(allowed, options.Value!.CanRequest);
        Assert.False(options.Value.CanRequest4k);
        var created = await fixture.Client.CreateRequestAsync(User, new("movie", 9, null, false), default);
        Assert.Equal(allowed ? 1 : 0, fixture.Posts);
        Assert.Equal(allowed, created.Value is not null);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public async Task OtherUsersRequestsReserveVariantIncludingFailedButNotDeclinedOrCompleted(int status, bool allowed)
    {
        using var fixture = new Fixture(() => Detail(requests: $$$"""[{"status":{{{status}}},"is4k":false,"requestedBy":{"id":999,"email":"private@example.test"}}]"""), true);
        var options = (await fixture.Client.GetRequestOptionsAsync(User, "movie", 9, default)).Value!;
        Assert.Equal(allowed, options.CanRequest);
        Assert.True(options.CanRequest4k);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(options));
        await fixture.Client.CreateRequestAsync(User, new("movie", 9, null, false), default);
        Assert.Equal(allowed ? 1 : 0, fixture.Posts);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task SeriesAggregateNeverSuppressesNewSeasonsAndMixedSelectionNeverPosts(int status)
    {
        using var fixture = new Fixture(() => Detail(status, 5,
            """[{"status":1,"is4k":false,"seasons":[{"seasonNumber":2}],"requestedBy":{"id":999}}]""",
            """[{"seasonNumber":1,"status":5,"status4k":1},{"seasonNumber":3,"status":3,"status4k":5}]"""), true);
        var options = (await fixture.Client.GetRequestOptionsAsync(User, "tv", 9, default)).Value!;
        Assert.True(options.CanRequest);
        Assert.Equal(new[] { 4 }, options.Seasons);
        Assert.Equal(new[] { 1, 2, 4 }, options.Seasons4k);
        foreach (var selected in new[] { new[] { 1 }, new[] { 2, 3 }, new[] { 1, 4 } })
            Assert.Equal(SeerrFailure.AlreadyRequested, (await fixture.Client.CreateRequestAsync(User, new("tv", 9, selected, false), default)).Failure);
        Assert.Equal(0, fixture.Posts);
        Assert.NotNull((await fixture.Client.CreateRequestAsync(User, new("tv", 9, [4], false), default)).Value);
        Assert.Equal(1, fixture.Posts);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public async Task TvRequestRetrySemanticsAndAllIneligibleSelections(int requestStatus, bool eligible)
    {
        using var fixture = new Fixture(() => Detail(4, 1,
            $$$"""[{"status":{{{requestStatus}}},"is4k":false,"seasons":[{"seasonNumber":4}]}]""",
            """[{"seasonNumber":1,"status":5,"status4k":1},{"seasonNumber":2,"status":2,"status4k":1},{"seasonNumber":3,"status":3,"status4k":1}]"""));
        var options = (await fixture.Client.GetRequestOptionsAsync(User, "tv", 9, default)).Value!;
        Assert.Equal(eligible, options.CanRequest);
        Assert.Equal(eligible ? new[] { 4 } : Array.Empty<int>(), options.Seasons);
        Assert.Equal(eligible, (await fixture.Client.CreateRequestAsync(User, new("tv", 9, [4], false), default)).Value is not null);
        Assert.Equal(eligible ? 1 : 0, fixture.Posts);
    }

    [Theory]
    [InlineData("{\"status\":1,\"status4k\":1}")]
    [InlineData("{\"status\":1,\"requests\":[{\"status\":1}]}")]
    [InlineData("{\"status\":1,\"requests\":null}")]
    public async Task IncompleteEligibilityFailsClosed(string info)
    {
        using var fixture = new Fixture(() => "{\"id\":9,\"title\":\"Unknown\",\"mediaInfo\":" + info + "}");
        Assert.Null((await fixture.Client.GetRequestOptionsAsync(User, "movie", 9, default)).Value);
        Assert.Null((await fixture.Client.CreateRequestAsync(User, new("movie", 9, null, false), default)).Value);
        Assert.Equal(0, fixture.Posts);
    }

    [Fact]
    public async Task DefaultLegacyAndPersistedOptInGateForgedAndAlreadyOpenForms()
    {
        Assert.False(new PluginConfiguration().Enable4kRequests);
        Assert.False(JsonSerializer.Deserialize<PluginConfiguration>("{}")!.Enable4kRequests);
        var saved = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(new PluginConfiguration { Enable4kRequests = true }))!;
        Assert.True(saved.Enable4kRequests);
        using var fixture = new Fixture(() => Detail(5));
        Assert.False((await fixture.Client.GetRequestOptionsAsync(User, "movie", 9, default)).Value!.CanRequest4k);
        Assert.Equal(SeerrFailure.PermissionDenied, (await fixture.Client.CreateRequestAsync(User, new("movie", 9, null, true), default)).Failure);
        fixture.Enabled = saved.Enable4kRequests;
        var options = (await fixture.Client.GetRequestOptionsAsync(User, "movie", 9, default)).Value!;
        Assert.False(options.CanRequest);
        Assert.True(options.CanRequest4k);
        fixture.OnDetail = () => fixture.Enabled = false;
        Assert.Equal(SeerrFailure.PermissionDenied, (await fixture.Client.CreateRequestAsync(User, new("movie", 9, null, true), default)).Failure);
        Assert.Equal(0, fixture.Posts);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(5, false)]
    [InlineData(99, false)]
    public async Task OptedIn4kDoesNotIgnoreVariantState(int status4k, bool allowed)
    {
        using var fixture = new Fixture(() => Detail(5, status4k), true);
        Assert.Equal(allowed, (await fixture.Client.GetRequestOptionsAsync(User, "movie", 9, default)).Value!.CanRequest4k);
        Assert.Equal(allowed, (await fixture.Client.CreateRequestAsync(User, new("movie", 9, null, true), default)).Value is not null);
        Assert.Equal(allowed ? 1 : 0, fixture.Posts);
    }

    [Fact]
    public void SettingPersistsInJellyfinXmlConfiguration()
    {
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var legacy = new System.IO.StringReader("<PluginConfiguration />");
        Assert.False(((PluginConfiguration)serializer.Deserialize(legacy)!).Enable4kRequests);
        foreach (var enabled in new[] { true, false })
        {
            using var writer = new System.IO.StringWriter();
            serializer.Serialize(writer, new PluginConfiguration { Enable4kRequests = enabled });
            using var reader = new System.IO.StringReader(writer.ToString());
            Assert.Equal(enabled, ((PluginConfiguration)serializer.Deserialize(reader)!).Enable4kRequests);
        }
    }

    [Fact]
    public async Task StaleOptionsAndConcurrentUsersRecheckFreshState()
    {
        var state = 1;
        using var fixture = new Fixture(() => Detail(state));
        Assert.True((await fixture.Client.GetRequestOptionsAsync(User, "movie", 9, default)).Value!.CanRequest);
        state = 2;
        Assert.Null((await fixture.Client.CreateRequestAsync(User, new("movie", 9, null, false), default)).Value);
        state = 1;
        fixture.OnPost = () => state = 2;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Client.CreateRequestAsync(Guid.NewGuid(), new("movie", 9, null, false), default)));
        Assert.Single(results, r => r.Value is not null);
        Assert.Equal(1, fixture.Posts);
    }

    [Fact]
    public async Task FreshLocalCheckBeforePostCanVetoStaleMissingState()
    {
        using var fixture = new Fixture(() => Detail());
        var result = await fixture.Client.CreateRequestAsync(User, new("movie", 9, null, false), default, () => SeerrFailure.AlreadyRequested);
        Assert.Equal(SeerrFailure.AlreadyRequested, result.Failure);
        Assert.Equal(0, fixture.Posts);
    }

    private sealed class Fixture : IDisposable
    {
        public bool Enabled;
        public int Posts;
        public Action? OnDetail;
        public Action? OnPost;
        public SeerrClient Client { get; }
        private readonly HttpClient _http;
        public Fixture(Func<string> detail, bool enabled = false)
        {
            Enabled = enabled;
            _http = new HttpClient(new Handler(async request =>
            {
                await Task.Yield();
                if (request.Method == HttpMethod.Post) { Posts++; OnPost?.Invoke(); return Json("{\"id\":91,\"status\":1}", HttpStatusCode.Created); }
                if (request.RequestUri!.AbsolutePath.Contains("/user/")) return Json("{\"id\":42,\"permissions\":2}");
                OnDetail?.Invoke();
                return Json(detail());
            }));
            Client = new SeerrClient(_http, SeerrOptions.FromConfiguration(new PluginConfiguration
                { SeerrEnabled = true, SeerrBaseUrl = "https://synthetic.invalid", SeerrApiKey = "synthetic" }), fourKEnabled: () => Enabled);
        }
        public void Dispose() => _http.Dispose();
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request); }
}
