using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Rowan.Jellyfin.Plugin.Assistant;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Discovery;
using Rowan.Jellyfin.Plugin.Web;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Assistant;

public sealed class AssistantTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "3pic-fin-ai-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private AiStore Store => new(_directory);
    private static PluginConfiguration Config => new() { AiEnabled = true, AiBaseUrl = "http://ollama.test:11434/v1", AiModel = "tool-model" };
    private static AiOptions Options => AiOptions.From(Config)!;
    private static AiCard Show => new("tv:9", "Synthetic show", "tv", 9, Date: "2025-01-01", PosterPath: "/show.jpg");
    private static AiThread Proposed() => new() { Messages = [new() { Proposals = [new() { Card = Show, Selection = new("tv", 9, [2, 3], false) }] }] };
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void OptionsAreDefaultOffAndSupportLanSubpathsWithoutCredentialsInUrls()
    {
        Assert.Null(AiOptions.From(new()));
        Assert.Equal("http://ollama.test:11434/v1/chat/completions", Options.EndpointUri.AbsoluteUri);
        var config = Config; config.AiBaseUrl = "https://proxy.test/jellyfin/ai/v1/"; config.AiApiKey = "fake-test-key";
        Assert.Equal("https://proxy.test/jellyfin/ai/v1/chat/completions", AiOptions.From(config)!.EndpointUri.AbsoluteUri);
        config.AiDailyTurnLimit = 0; Assert.Equal(1, AiOptions.From(config)!.DailyLimit);
        Assert.DoesNotContain("fake-test-key", AiJson.Write(AiOptions.From(config)!));
        Assert.DoesNotContain("fake-test-key", AiOptions.From(config)!.ToString());
    }

    [Theory]
    [InlineData("gpt-6-sol", "auto", true)]
    [InlineData("gpt-6.1-sol", "auto", true)]
    [InlineData("gpt-6-luna", "chat", false)]
    [InlineData("gateway-model", "responses", true)]
    [InlineData("gpt-5.4-mini", "auto", false)]
    public void ApiModeSelectsTheEndpointAndDefaultsToLowReasoning(string model, string mode, bool responses)
    {
        var config = Config; config.AiModel = model; config.AiApiMode = mode;
        var options = AiOptions.From(config)!;
        Assert.Equal(responses, options.UseResponses);
        Assert.Equal(responses ? "/v1/responses" : "/v1/chat/completions", options.EndpointUri.AbsolutePath);
        Assert.Equal("low", options.ReasoningEffort);
        config.AiReasoningEffort = "default";
        Assert.Null(AiOptions.From(config)!.ReasoningEffort);
        config.AiReasoningEffort = "not-supported"; Assert.Null(AiOptions.From(config));
        config.AiReasoningEffort = "low"; config.AiApiMode = "unknown"; Assert.Null(AiOptions.From(config));
    }

    [Theory]
    [InlineData(false, "low")]
    [InlineData(false, "none")]
    [InlineData(false, null)]
    [InlineData(true, "low")]
    [InlineData(true, "high")]
    [InlineData(true, null)]
    public async Task ReasoningEffortIsSentInTheSelectedProtocolOrExplicitlyOmitted(bool responses, string? effort)
    {
        using var http = Http(async request =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            if (responses)
            {
                Assert.False(body.TryGetProperty("reasoning_effort", out _));
                Assert.Equal(effort is not null, body.TryGetProperty("reasoning", out var reasoning));
                if (effort is not null) Assert.Equal(effort, reasoning.GetProperty("effort").GetString());
                Assert.Equal(8192, body.GetProperty("max_output_tokens").GetInt32());
                return Json("""{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hello"}]}]}""");
            }
            Assert.False(body.TryGetProperty("reasoning", out _));
            Assert.Equal(effort is not null, body.TryGetProperty("reasoning_effort", out var parameter));
            if (effort is not null) Assert.Equal(effort, parameter.GetString());
            return Json("""{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"Hello"}}]}""");
        });
        Assert.Equal("Hello", (await new AiClient(http).Complete(Options with { UseResponses = responses, ReasoningEffort = effort }, [], AiTools.Definitions, default)).GetProperty("content").GetString());
    }

    [Fact]
    public async Task ResponsesToolLoopReplaysEncryptedReasoningAndCallIdsButNeverPersistsProviderState()
    {
        var config = Config; config.AiModel = "gpt-6-sol";
        var options = AiOptions.From(config)!; var step = 0;
        using var http = Http(async request =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            Assert.False(body.GetProperty("store").GetBoolean()); Assert.False(body.GetProperty("stream").GetBoolean());
            Assert.False(body.TryGetProperty("messages", out _)); Assert.False(body.TryGetProperty("previous_response_id", out _));
            Assert.False(body.TryGetProperty("conversation", out _)); Assert.False(body.TryGetProperty("max_completion_tokens", out _));
            var tool = body.GetProperty("tools")[0];
            Assert.Equal("search_library", tool.GetProperty("name").GetString()); Assert.False(tool.GetProperty("strict").GetBoolean());
            Assert.False(tool.TryGetProperty("function", out _));
            Assert.Equal("reasoning.encrypted_content", body.GetProperty("include")[0].GetString());
            if (++step == 1) return Json("""
                {"status":"completed","output":[{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"cipher-do-not-persist"},
                {"type":"function_call","id":"fc_distinct_from_call_id","call_id":"taste_call","name":"remember_taste","arguments":"{\"preference\":\"Likes comedy\"}"}]}
                """);
            var input = body.GetProperty("input").EnumerateArray().ToArray();
            Assert.Contains(input, i => i.TryGetProperty("encrypted_content", out var cipher) && cipher.GetString() == "cipher-do-not-persist");
            var output = Assert.Single(input, i => i.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            Assert.Equal("taste_call", output.GetProperty("call_id").GetString()); Assert.Contains("remembered", output.GetProperty("output").GetString());
            return Json("""{"status":"completed","output":[{"type":"message","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"A comedy tonight?"}]}]}""");
        });
        var store = Store; var engine = new AiConversation(new(http), new(null!, null!, null!, null!), null!, store);
        await store.Use(Alice, async data =>
        {
            data.Personalization = false; var thread = new AiThread(); data.Threads.Add(thread);
            await engine.Reply(Alice, data, thread, "Something funny", options, () => true, default);
            Assert.Equal("A comedy tonight?", thread.Messages.Last().Text); Assert.Single(data.Memory);
            return 0;
        }, default);
        Assert.Equal(2, step);
        var saved = await File.ReadAllTextAsync(Path.Combine(_directory, Alice.ToString("N") + ".json"));
        foreach (var secret in new[] { "cipher-do-not-persist", "fc_distinct", "rs_1", "response_output", "encrypted_content" }) Assert.DoesNotContain(secret, saved);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"completed\"}")]
    [InlineData("{\"status\":\"incomplete\",\"output\":[]}")]
    [InlineData("{\"status\":\"completed\",\"output\":[{\"type\":\"web_search_call\"}]}")]
    public async Task ResponsesRejectsMalformedIncompleteAndUnconfiguredProviderTools(string json)
    {
        using var http = Http(_ => Task.FromResult(Json(json)));
        await Assert.ThrowsAsync<AiException>(() => new AiClient(http).Complete(Options with { UseResponses = true }, [], AiTools.Definitions, default));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:password@provider.test/v1")]
    [InlineData("https://provider.test/v1?key=secret")]
    [InlineData("https://provider.test/v1#fragment")]
    [InlineData("not-a-url")]
    public void UnsafeConfiguredEndpointIsRejected(string url)
    { var config = Config; config.AiBaseUrl = url; Assert.Null(AiOptions.From(config)); }

    [Fact]
    public void RestrictionsDisableAssistantAndFullLibraryQueryDoesNotInheritHomeLimits()
    {
        var user = new User("test", "test", "test");
        Assert.True(AiLibrary.Allowed(user));
        user.MaxParentalRatingScore = 100; Assert.False(AiLibrary.Allowed(user)); user.MaxParentalRatingScore = null;
        foreach (var kind in new[] { PreferenceKind.BlockedTags, PreferenceKind.AllowedTags, PreferenceKind.BlockUnratedItems })
        { user.SetPreference(kind, new[] { "restricted" }); Assert.False(AiLibrary.Allowed(user)); user.SetPreference(kind, Array.Empty<string>()); }
        user.SetPermission(PermissionKind.IsDisabled, true); Assert.False(AiLibrary.Allowed(user));
        var roots = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToArray();
        var query = AiLibrary.SearchQuery(user, roots, "film", "Comedy", "movie", false, 200);
        Assert.Same(user, query.User); Assert.Equal(roots, query.AncestorIds); Assert.Equal(3980, query.StartIndex);
        Assert.Equal(20, query.Limit); Assert.Equal("film", query.SearchTerm); Assert.Equal(new[] { "Comedy" }, query.Genres);
        Assert.False(query.IsPlayed); Assert.False(query.IsVirtualItem);
    }

    [Fact]
    public async Task StorageAndControllerScopesNeverAcceptAnotherUsersConversationOrMemoryIds()
    {
        var store = Store; var thread = Proposed(); var memory = new AiMemory(Guid.NewGuid(), "Likes slow science fiction");
        await store.Use(Alice, data => { data.Threads.Add(thread); data.Memory.Add(memory); return Task.FromResult(0); }, default);
        var restart = Store;
        Assert.Equal(thread.Id, await restart.Use(Alice, data => Task.FromResult(Assert.Single(data.Threads).Id), default));
        await restart.Use(Bob, data => { Assert.Empty(data.Threads); Assert.Empty(data.Memory); return Task.FromResult(0); }, default);
        var bob = Controller(restart, Bob);
        Assert.Equal(404, Assert.IsType<ObjectResult>(await bob.Thread(thread.Id, default)).StatusCode);
        Assert.Equal(404, Assert.IsType<ObjectResult>(await bob.Memory(new(memory.Id, "tamper"), default)).StatusCode);
        Assert.Equal(404, Assert.IsType<ObjectResult>(await bob.Confirm(new(thread.Id, thread.Messages[0].Proposals[0].Id, true), default)).StatusCode);
        Assert.Equal("private, no-store", bob.Response.Headers.CacheControl.ToString());
        var alice = Controller(restart, Alice);
        Assert.Equal(thread.Id, Assert.IsType<AiThread>(Assert.IsType<OkObjectResult>(await alice.Thread(thread.Id, default)).Value).Id);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_directory, Alice.ToString("N") + ".json")));
    }

    [Fact]
    public async Task AnonymousUserlessConflictingUnknownAndRestrictedIdentitiesAreDenied()
    {
        Assert.NotNull(typeof(AiController).GetCustomAttribute<AuthorizeAttribute>());
        foreach (var claims in new[] { Array.Empty<Claim>(), new[] { new Claim("Jellyfin-UserId", Alice.ToString()), new Claim("Jellyfin-UserId", Bob.ToString()) } })
        {
            var controller = Controller(Store, Alice); controller.HttpContext.User = new(new ClaimsIdentity(claims, "test"));
            Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.State(default)).StatusCode);
        }
        var restricted = Controller(Store, Alice, restricted: true);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await restricted.State(default)).StatusCode);
        var unknown = Controller(Store, Guid.NewGuid());
        Assert.Equal(403, Assert.IsType<ObjectResult>(await unknown.State(default)).StatusCode);
        var disabled = Controller(Store, Alice, config: new());
        Assert.Equal(404, Assert.IsType<ObjectResult>(await disabled.State(default)).StatusCode);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task RetentionSweepExpiresInactiveThreadsButKeepsPreferencesAndDeletesOnlyOwnRecords()
    {
        Directory.CreateDirectory(_directory);
        var old = Proposed(); old.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-31);
        var recent = Proposed(); var data = new AiUserData { Threads = [old, recent], Memory = [new(Guid.NewGuid(), "Likes comedy")] };
        await File.WriteAllTextAsync(Path.Combine(_directory, Alice.ToString("N") + ".json"), AiJson.Write(data));
        await Store.Sweep(default);
        await Store.Use(Alice, saved => { Assert.Equal(recent.Id, Assert.Single(saved.Threads).Id); Assert.Single(saved.Memory); return Task.FromResult(0); }, default);
        await Controller(Store, Alice).DeleteThread(recent.Id, default);
        await Store.Use(Alice, saved => { Assert.Empty(saved.Threads); Assert.Single(saved.Memory); return Task.FromResult(0); }, default);
    }

    [Fact]
    public async Task PartialSettingsUpdatesPreserveOtherPrivacyChoicesAndReturnCanonicalValues()
    {
        var store = Store;
        var controller = Controller(store, Alice);
        // Separate stale tabs change different controls; neither submits the other's cached value.
        Assert.IsType<OkObjectResult>(await controller.Settings(new(Personalization: false), default));
        var patch = JsonSerializer.Deserialize<AiSettings>("{\"learnMemory\":false}", AiJson.Options)!;
        Assert.Null(patch.Personalization);
        var result = Assert.IsType<OkObjectResult>(await controller.Settings(patch, default));
        var saved = JsonSerializer.SerializeToElement(result.Value, AiJson.Options);
        Assert.False(saved.GetProperty("personalization").GetBoolean());
        Assert.False(saved.GetProperty("learnMemory").GetBoolean());
        Assert.IsType<OkObjectResult>(await controller.Settings(new(), default));
        await store.Use(Alice, data =>
        {
            Assert.False(data.Personalization); Assert.False(data.LearnMemory);
            return Task.FromResult(0);
        }, default);
        // Full updates from existing clients remain supported.
        Assert.IsType<OkObjectResult>(await controller.Settings(new(true, true), default));
        await store.Use(Alice, data =>
        {
            Assert.True(data.Personalization); Assert.True(data.LearnMemory);
            return Task.FromResult(0);
        }, default);
    }

    [Fact]
    public async Task AccountLockRejectsConcurrentEditsWithoutQueueingOrLosingData()
    {
        var store = Store; var started = new TaskCompletionSource(); var finish = new TaskCompletionSource();
        var pending = store.Use(Alice, async data => { started.SetResult(); await finish.Task; data.Personalization = false; return 0; }, default);
        await started.Task;
        var failure = await Assert.ThrowsAsync<AiException>(() => store.Use(Alice, _ => Task.FromResult(0), default));
        Assert.Equal(429, failure.Status); finish.SetResult(); await pending;
        Assert.False(await Store.Use(Alice, d => Task.FromResult(d.Personalization), default));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"threads\":null}")]
    [InlineData("{\"threads\":[{\"messages\":null}]}")]
    public async Task DamagedAccountFilesAreNotOverwrittenAndDoNotCrashRetention(string contents)
    {
        Directory.CreateDirectory(_directory); var path = Path.Combine(_directory, Alice.ToString("N") + ".json");
        await File.WriteAllTextAsync(path, contents);
        await Store.Sweep(default);
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
        Assert.Equal(503, (await Assert.ThrowsAsync<AiException>(() => Store.Use(Alice, _ => Task.FromResult(0), default))).Status);
    }

    [Fact]
    public async Task FreshCatalogBrowsingFiltersAdultAndBlockedTitlesAndReportsContinuation()
    {
        using var http = Http(request => Task.FromResult(Json(request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/") ? "{\"id\":7}" : """
            {"page":2,"totalPages":10,"results":[{"id":1,"title":"Safe fixture","adult":false},{"id":2,"title":"Adult fixture","adult":true},{"id":3,"title":"Blocked fixture","adult":false,"mediaInfo":{"status":6}}]}
            """)));
        var seerr = new SeerrClient(http, SeerrOptions.FromConfiguration(new() { SeerrEnabled = true, SeerrBaseUrl = "http://seerr.test", SeerrApiKey = "fake-key" }));
        var tools = new AiTools(null!, seerr, null!, null!);
        var result = JsonSerializer.SerializeToElement(await tools.Run(new(Alice, new(), new(), () => true), "browse_catalog", Args("{\"feed\":\"movies\",\"page\":2}"), default), AiJson.Options);
        Assert.Equal("Safe fixture", Assert.Single(result.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal(3, result.GetProperty("nextPage").GetInt32()); Assert.False(result.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task OnlyExactUserConfirmationCanWriteAndItIsDurableSingleUse()
    {
        var store = Store; var thread = Proposed(); var proposal = thread.Messages[0].Proposals[0]; var posts = 0;
        var confirmation = new AiRequestConfirmation(store, async (user, selection, token) =>
        {
            posts++; Assert.Equal(Alice, user); Assert.Equal(new[] { 2, 3 }, selection.Seasons);
            var disk = JsonSerializer.Deserialize<AiUserData>(await File.ReadAllTextAsync(Path.Combine(_directory, Alice.ToString("N") + ".json")), AiJson.Options)!;
            Assert.Equal("submitting", disk.Threads[0].Messages[0].Proposals[0].State);
            return new(new(42, 2), null);
        }, (_, _, _, _) => Task.FromResult(true));
        await store.Use(Alice, async data => { data.Threads.Add(thread); await confirmation.Confirm(Alice, data, thread, proposal.Id, true, () => true, default); return 0; }, default);
        await store.Use(Alice, async data => { await confirmation.Confirm(Alice, data, data.Threads[0], proposal.Id, true, () => true, default); return 0; }, default);
        Assert.Equal(1, posts); Assert.Equal("submitted", proposal.State);
    }

    [Fact]
    public async Task AccountRevocationDuringFreshTvEligibilityReadPreventsThePost()
    {
        var allowed = true; var posts = 0; var store = Store;
        using var http = Http(request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; return Task.FromResult(Json("{}", HttpStatusCode.Created)); }
            if (request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/")) return Task.FromResult(Json("{\"id\":7,\"permissions\":32}"));
            allowed = false;
            return Task.FromResult(Json("""{"id":9,"name":"Synthetic show","seasons":[{"seasonNumber":2},{"seasonNumber":3}]}"""));
        });
        var seerr = new SeerrClient(http, SeerrOptions.FromConfiguration(new() { SeerrEnabled = true, SeerrBaseUrl = "http://seerr.test", SeerrApiKey = "fake-key" }));
        var service = new AiRequestConfirmation(store, seerr, null!, null!, null!, _ => allowed);
        var thread = Proposed(); var p = thread.Messages[0].Proposals[0];
        await store.Use(Alice, async data => { data.Threads.Add(thread); await service.Confirm(Alice, data, thread, p.Id, true, () => true, default); return 0; }, default);
        Assert.Equal(0, posts); Assert.Equal("denied", p.State);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CancelledAndExpiredProposalsNeverSubmit(bool cancel, bool expired)
    {
        var thread = Proposed(); var p = thread.Messages[0].Proposals[0];
        if (expired) p.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var service = new AiRequestConfirmation(Store, (_, _, _) => throw new Exception("must not submit"), (_, _, _, _) => Task.FromResult(false));
        await Store.Use(Alice, async data => { data.Threads.Add(thread); await service.Confirm(Alice, data, thread, p.Id, !cancel, () => true, default); return 0; }, default);
        Assert.Equal(cancel ? "cancelled" : "expired", p.State);
    }

    [Theory]
    [InlineData("quota", "denied")]
    [InlineData("timeout", "unknown")]
    [InlineData("unverified", "unknown")]
    public async Task DeniedInterruptedAndUnverifiedSubmissionsNeverRetry(string outcome, string state)
    {
        var store = Store; var thread = Proposed(); var p = thread.Messages[0].Proposals[0]; var posts = 0;
        var service = new AiRequestConfirmation(store, (_, _, _) =>
        {
            posts++;
            return outcome switch
            {
                "timeout" => Task.FromException<SeerrCreateResult>(new OperationCanceledException()),
                "quota" => Task.FromResult(new SeerrCreateResult(null, SeerrFailure.PermissionDenied)),
                _ => Task.FromResult(new SeerrCreateResult(new(42, 2), null))
            };
        }, (_, _, _, _) => Task.FromResult(false));
        await store.Use(Alice, async data => { data.Threads.Add(thread); await service.Confirm(Alice, data, thread, p.Id, true, () => true, default); return 0; }, default);
        await store.Use(Alice, async data => { await service.Confirm(Alice, data, data.Threads[0], p.Id, true, () => true, default); return 0; }, default);
        Assert.Equal(1, posts); Assert.Equal(state, p.State);
    }

    [Fact]
    public async Task WireProtocolIsStatelessBoundedAndDoesNotForwardRawProviderErrors()
    {
        foreach (var legacy in new[] { false, true })
        {
            using var http = Http(async request =>
            {
                Assert.Equal("fake-test-key", request.Headers.Authorization?.Parameter);
                var wire = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                Assert.False(wire.GetProperty("store").GetBoolean()); Assert.False(wire.GetProperty("stream").GetBoolean());
                Assert.Equal(2048, wire.GetProperty(legacy ? "max_tokens" : "max_completion_tokens").GetInt32());
                Assert.False(wire.TryGetProperty("conversation", out _)); Assert.False(wire.TryGetProperty("previous_response_id", out _));
                return Json("private-provider-response-key-secret", HttpStatusCode.Unauthorized);
            });
            var failure = await Assert.ThrowsAsync<AiException>(() => new AiClient(http).Complete(Options with { ApiKey = "fake-test-key", LegacyTokenLimit = legacy }, [], AiTools.Definitions, default));
            Assert.DoesNotContain("secret", failure.Message);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{}}]}")]
    public async Task MalformedOrIncompleteModelOutputFailsClosed(string json)
    {
        using var http = Http(_ => Task.FromResult(Json(json)));
        await Assert.ThrowsAsync<AiException>(() => new AiClient(http).Complete(Options, [], AiTools.Definitions, default));
    }

    [Fact]
    public async Task ModelCanPrepareSeasonCardAndRememberTasteButCannotSubmitOrSpoofAccounts()
    {
        var posts = 0;
        using var http = Http(request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; throw new Exception("Model must never submit"); }
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath.Contains("/user/jellyfin/") ? "{\"id\":7,\"permissions\":32}" :
                """{"id":9,"name":"Synthetic show","seasons":[{"seasonNumber":1},{"seasonNumber":2},{"seasonNumber":3}]}"""));
        });
        var seerr = new SeerrClient(http, SeerrOptions.FromConfiguration(new() { SeerrEnabled = true, SeerrBaseUrl = "http://seerr.test", SeerrApiKey = "fake-key" }));
        var tools = new AiTools(null!, seerr, null!, null!, (_, key, _) => Task.FromResult<AiCard?>(key == "tv:9" ? Show : null));
        var data = new AiUserData(); var response = new AiMessage(); var turn = new AiTools.Turn(Alice, data, response, () => true);
        await tools.Run(turn, "prepare_request", Args("{\"key\":\"tv:9\",\"seasons\":[2,3]}"), default);
        Assert.Equal(new[] { 2, 3 }, Assert.Single(response.Proposals).Selection.Seasons); Assert.Equal(0, posts);
        await tools.Run(turn, "remember_taste", Args("{\"preference\":\"Likes quiet science fiction\"}"), default);
        Assert.Single(data.Memory);
        var invalid = await tools.Run(turn, "prepare_request", Args("{\"key\":\"tv:9\",\"seasons\":[1],\"userId\":999}"), default);
        Assert.Contains("Unknown", AiJson.Write(invalid)); Assert.Single(response.Proposals);
        await Assert.ThrowsAsync<AiException>(() => tools.Run(turn, "submit_request", Args("{}"), default));
        data.LearnMemory = false;
        await tools.Run(turn, "remember_taste", Args("{\"preference\":\"Must not persist\"}"), default); Assert.Single(data.Memory);
        await Assert.ThrowsAsync<AiException>(() => tools.Run(new(Alice, data, response, () => false), "remember_taste", Args("{}"), default));
    }

    [Fact]
    public async Task HouseholdNamesAreOptInAllowlistedAndNeverIncludeEmailOrOtherUserData()
    {
        var reads = 0; var enabled = false;
        using var http = Http(_ => { reads++; return Task.FromResult(Json("""
            {"pageInfo":{"page":1,"pages":1},"results":[{"id":12,"status":2,"is4k":false,"seasons":[{"seasonNumber":2}],"media":{"tmdbId":9,"mediaType":"tv"},"requestedBy":{"id":99,"displayName":"Housemate","email":"secret@example.test","token":"private-key"}}]}
            """)); });
        var seerr = new SeerrClient(http, SeerrOptions.FromConfiguration(new() { SeerrEnabled = true, SeerrBaseUrl = "http://seerr.test", SeerrApiKey = "fake-key" }));
        var tools = new AiTools(null!, seerr, null!, null!, (_, _, _) => Task.FromResult<AiCard?>(Show), () => enabled);
        var turn = new AiTools.Turn(Alice, new(), new(), () => true);
        await tools.Run(turn, "requesters", Args("{\"key\":\"tv:9\"}"), default); Assert.Equal(0, reads);
        enabled = true;
        var result = AiJson.Write(await tools.Run(turn, "requesters", Args("{\"key\":\"tv:9\"}"), default));
        Assert.Contains("Housemate", result); Assert.Contains("\"complete\":true", result);
        Assert.DoesNotContain("secret", result); Assert.DoesNotContain("private-key", result); Assert.DoesNotContain("requestedBy", result);
    }

    [Fact]
    public async Task ConversationReplaysOnlyItsOwnHistoryBoundsToolsAndPersistsDailyLimit()
    {
        var step = 0; var payloads = new List<string>();
        using var http = Http(async request =>
        {
            payloads.Add(await request.Content!.ReadAsStringAsync()); step++;
            return Json(step == 1 ? """
                {"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"reasoning_content":"do not replay reasoning","tool_calls":[{"id":"call_one","type":"function","function":{"name":"remember_taste","arguments":"{\"preference\":\"Likes comedy\"}"}}]}}]}
                """ : """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"Do you want a film or a series?"}}]}""");
        });
        var store = Store; var engine = new AiConversation(new(http), new(null!, null!, null!, null!), null!, store);
        await store.Use(Alice, async data =>
        {
            data.Personalization = false; var thread = new AiThread(); data.Threads.Add(thread);
            await engine.Reply(Alice, data, thread, "Something funny", Options with { DailyLimit = 1 }, () => true, default);
            Assert.Equal("Do you want a film or a series?", thread.Messages.Last().Text); Assert.Single(data.Memory);
            var failure = await Assert.ThrowsAsync<AiException>(() => engine.Reply(Alice, data, thread, "Again", Options with { DailyLimit = 1 }, () => true, default));
            Assert.Equal(429, failure.Status); return 0;
        }, default);
        Assert.Contains("\"role\":\"tool\"", payloads.Last()); Assert.DoesNotContain("do not replay reasoning", payloads.Last());
        await Store.Use(Alice, data => { Assert.Equal(1, data.Turns); return Task.FromResult(0); }, default);
        await Store.Use(Bob, data => { Assert.Empty(data.Threads); Assert.Empty(data.Memory); Assert.Equal(0, data.Turns); return Task.FromResult(0); }, default);
    }

    [Fact]
    public void AssistantAssetsAndInjectionAreIndependentDefaultOffAndIdempotent()
    {
        Assert.False(HomeAdapterRegistration.ShouldInject(new() { HomeEnabled = false, DiscoveryPageEnabled = false }));
        var enabled = new PluginConfiguration { HomeEnabled = false, DiscoveryPageEnabled = false, AiEnabled = true };
        Assert.True(HomeAdapterRegistration.ShouldInject(enabled));
        var html = "<html><body>Native</body></html>";
        var transformed = HomeAdapterRegistration.TransformIndex(new JObject { ["contents"] = html }, enabled: false, aiEnabled: true);
        Assert.Contains("data-threepic-fin-ai", transformed); Assert.DoesNotContain("data-threepic-fin-adapter", transformed);
        Assert.Equal(transformed, HomeAdapterRegistration.TransformIndex(new JObject { ["contents"] = transformed }, enabled: false, aiEnabled: true));
        var assets = new DiscoveryPageController(() => false, () => false, () => false, () => false, () => false, () => true);
        foreach (var name in new[] { "assistant.js", "assistant.css", "assistant-marked.js", "assistant-purify.js", "discovery.js", "discovery.html", "continuous-rail.js" })
        {
            var asset = Assert.IsType<FileStreamResult>(assets.GetAsset(name)); asset.FileStream.Dispose();
        }
        Assert.IsType<NotFoundResult>(assets.GetAsset("configPage.html"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AiConfirm>("{\"userId\":\"spoof\"}", AiJson.Options));
    }

    private static JsonElement Args(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public async Task StarterContextRespectsPersonalizationAndDoesNotSpendAnAiTurn()
    {
        var store = Store;
        await store.Use(Alice, data => { data.Personalization = false; return Task.FromResult(0); }, default);
        var result = Assert.IsType<OkObjectResult>(await Controller(store, Alice).Starters(default));
        var body = JsonSerializer.SerializeToElement(result.Value, AiJson.Options);
        Assert.Empty(body.GetProperty("seeds").EnumerateArray());
        await store.Use(Alice, data => { Assert.Equal(0, data.Turns); Assert.Empty(data.Threads); return Task.FromResult(0); }, default);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await Controller(store, Alice, restricted: true).Starters(default)).StatusCode);
    }

    [Fact]
    public async Task ToolBudgetEndsWithAnAnswerInsteadOfDiscardingSuccessfulWork()
    {
        var step = 0;
        using var http = Http(async request =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            if (++step == 10)
            {
                Assert.Empty(body.GetProperty("tools").EnumerateArray());
                return Json("""{"choices":[{"finish_reason":"stop","message":{"content":"Here are the verified results."}}]}""");
            }
            return Json("""{"choices":[{"finish_reason":"tool_calls","message":{"tool_calls":[{"id":"call_STEP","type":"function","function":{"name":"remember_taste","arguments":"{\"preference\":\"Likes comedy\"}"}}]}}]}""".Replace("STEP", step.ToString()));
        });
        var engine = new AiConversation(new(http), new(null!, null!, null!, null!), null!, Store);
        var data = new AiUserData { Personalization = false }; var thread = new AiThread(); data.Threads.Add(thread);
        await engine.Reply(Alice, data, thread, "Something funny", Options, () => true, default);
        Assert.Equal(10, step); Assert.Equal("Here are the verified results.", thread.Messages.Last().Text);
    }
    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpClient Http(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) => new(new Handler(response));
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request); }
    public class Users : DispatchProxy
    {
        public Func<Guid, User?> Find { get; set; } = _ => null;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "GetUserById" ? Find((Guid)args![0]!) : throw new NotSupportedException();
    }
    private static AiController Controller(AiStore store, Guid userId, bool restricted = false, PluginConfiguration? config = null)
    {
        var users = DispatchProxy.Create<IUserManager, Users>();
        ((Users)(object)users).Find = id => id == Alice || id == Bob ? new User("fixture", "fixture", "fixture") { MaxParentalRatingScore = restricted ? 100 : null } : null;
        return new(store, null!, null!, null!, users, null!, () => config ?? Config)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "test")) } } };
    }
}
