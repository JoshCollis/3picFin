using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Home;

namespace Rowan.Jellyfin.Plugin.Assistant;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiSend(Guid? ThreadId, string? Text, string? TimeZone = null, Guid? ItemId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiConfirm(Guid ThreadId, Guid ProposalId, bool Confirm);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiSettings(bool? Personalization = null, bool? LearnMemory = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiMemoryEdit(Guid? Id, string? Text);

[ApiController]
[Authorize]
[Route("3picFin/Ai")]
[RequestSizeLimit(16384)]
public sealed class AiController(AiStore store, AiConversation conversation, AiRequestConfirmation confirmation, AiClient client, IUserManager users, AiLibrary library,
    Func<PluginConfiguration?>? configuration = null, AiLiveTv? liveTv = null, AiTools? tools = null) : ControllerBase
{
    private PluginConfiguration? Configuration => configuration?.Invoke() ?? Plugin.Current?.Configuration;
    private bool Allowed(Guid id) => AiOptions.From(Configuration) is not null && AiLibrary.Allowed(users.GetUserById(id));
    private Guid Identity()
    {
        Response.Headers.CacheControl = "private, no-store";
        if (User.Identity?.IsAuthenticated != true || !RecentlyAddedPolicy.TryGetUserId(User, out var id) || !AiLibrary.Allowed(users.GetUserById(id)))
            throw new AiException("The assistant is unavailable for this account.", 403);
        if (AiOptions.From(Configuration) is null) throw new AiException("The assistant is disabled or not configured.", 404);
        return id;
    }

    private async Task<IActionResult> Run(Func<Guid, Task<object>> action)
    {
        try
        {
            var user = Identity(); var result = await action(user).ConfigureAwait(false);
            if (!Allowed(user)) throw new AiException("The assistant is unavailable for this account.", 403);
            return Ok(result);
        }
        catch (AiException e) { return StatusCode(e.Status, new { Error = e.Message }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return StatusCode(503, new { Error = "Assistant storage is unavailable." }); }
        catch (OperationCanceledException) { return StatusCode(408, new { Error = "Assistant operation was interrupted. Check any pending request outcome before retrying." }); }
    }

    [HttpGet("State")]
    public Task<IActionResult> State(CancellationToken token) => Run(async user => await store.Use<object>(user, data => Task.FromResult<object>(new
    {
        Threads = data.Threads.OrderByDescending(t => t.UpdatedAt).Select(t => new { t.Id, t.Title, t.UpdatedAt }),
        data.Memory, data.Personalization, data.LearnMemory,
        DailyLimit = CurrentOptions().DailyLimit,
        TurnsToday = data.UsageDay == DateOnly.FromDateTime(DateTime.UtcNow) ? data.Turns : 0,
        RetentionDays = 30
    }), token).ConfigureAwait(false));

    [HttpGet("Starters")]
    public Task<IActionResult> Starters(CancellationToken token) => Run(async user => await store.Use<object>(user, data =>
    {
        token.ThrowIfCancellationRequested();
        var activity = data.Personalization ? library.Personalize(user) : new AiViewingContext([], [], []);
        return Task.FromResult<object>(new
        {
            data.Personalization, sources = tools?.Context(user),
            seeds = activity.Favorites.Take(4).Concat(activity.Recent.Take(4)).Concat(activity.InProgress.Take(4).Select(p => p.Title)).DistinctBy(c => c.Key)
                .Select(c => new { title = AiJson.Clip(c.Title, 120), c.MediaType, c.Genres }).ToArray()
        });
    }, token).ConfigureAwait(false));

    [HttpGet("Threads/{id:guid}")]
    public Task<IActionResult> Thread(Guid id, CancellationToken token) => Run(async user => await store.Use<object>(user,
        data => Task.FromResult<object>(Find(data, id)), token).ConfigureAwait(false));

    [HttpPost("Message")]
    public Task<IActionResult> Message([FromBody] AiSend body, CancellationToken token) => Run(async user =>
    {
        var text = body.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000) throw new AiException("Enter a message of 1–2000 characters.", 400);
        TimeZoneInfo zone;
        try { zone = body.TimeZone is null ? TimeZoneInfo.Utc : body.TimeZone.Length <= 100 ? TimeZoneInfo.FindSystemTimeZoneById(body.TimeZone) : throw new TimeZoneNotFoundException(); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new AiException("Provide a valid time zone.", 400); }
        return await store.Use<object>(user, async data =>
        {
            var pageTitle = body.ItemId is { } itemId && itemId != Guid.Empty ? library.Get(user, itemId) ??
                (liveTv?.IsAvailable(user) == true ? liveTv.GetChannel(user, itemId) : null) : null;
            var thread = body.ThreadId is { } id ? Find(data, id) : new AiThread();
            if (body.ThreadId is null) data.Threads.Add(thread);
            return await conversation.Reply(user, data, thread, text, CurrentOptions(), () => Allowed(user), token, zone, pageTitle).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    });

    [HttpGet("Library/{id:guid}")]
    public Task<IActionResult> Library(Guid id) => Run(user => Task.FromResult<object>(library.Get(user, id) ?? throw new AiException("Library item is no longer visible.", 404)));

    [HttpGet("Channels/{id:guid}")]
    public Task<IActionResult> Channel(Guid id) => Run(user => Task.FromResult<object>(liveTv?.GetChannel(user, id) ?? throw new AiException("Channel is no longer visible.", 404)));

    [HttpPost("Confirm")]
    public Task<IActionResult> Confirm([FromBody] AiConfirm body, CancellationToken token) => Run(async user => await store.Use<object>(user, async data =>
    {
        var thread = Find(data, body.ThreadId);
        await confirmation.Confirm(user, data, thread, body.ProposalId, body.Confirm, () => Allowed(user), token).ConfigureAwait(false);
        if (!Allowed(user)) throw new AiException("The assistant is unavailable for this account.", 403);
        return thread;
    }, token).ConfigureAwait(false));

    [HttpDelete("Threads/{id:guid}")]
    public Task<IActionResult> DeleteThread(Guid id, CancellationToken token) => Run(async user => await store.Use<object>(user, data =>
    { data.Threads.Remove(Find(data, id)); return Task.FromResult<object>(new { Deleted = true }); }, token).ConfigureAwait(false));

    [HttpPost("Settings")]
    public Task<IActionResult> Settings([FromBody] AiSettings body, CancellationToken token) => Run(async user => await store.Use<object>(user, data =>
    {
        if (body.Personalization is { } personalization) data.Personalization = personalization;
        if (body.LearnMemory is { } learnMemory) data.LearnMemory = learnMemory;
        return Task.FromResult<object>(new { data.Personalization, data.LearnMemory });
    }, token).ConfigureAwait(false));

    [HttpPost("Memory")]
    public Task<IActionResult> Memory([FromBody] AiMemoryEdit body, CancellationToken token) => Run(async user => await store.Use<object>(user, data =>
    {
        var text = body.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 160 || text.Any(char.IsControl)) throw new AiException("Use a preference of 1–160 characters.", 400);
        if (body.Id is { } id)
        {
            var index = data.Memory.FindIndex(m => m.Id == id);
            if (index < 0) throw new AiException("Preference not found.", 404);
            data.Memory[index] = new(id, text);
        }
        else
        {
            if (data.Memory.Count >= 20) throw new AiException("Delete an old preference before adding another.", 409);
            data.Memory.Add(new(Guid.NewGuid(), text));
        }
        return Task.FromResult<object>(data.Memory);
    }, token).ConfigureAwait(false));

    [HttpDelete("Memory/{id:guid}")]
    public Task<IActionResult> Forget(Guid id, CancellationToken token) => Run(async user => await store.Use<object>(user, data =>
    { data.Memory.RemoveAll(m => m.Id == id); return Task.FromResult<object>(new { Deleted = true }); }, token).ConfigureAwait(false));

    [HttpPost("Test")]
    public Task<IActionResult> Test(CancellationToken token) => Run(async user =>
    {
        if (users.GetUserById(user)?.HasPermission(PermissionKind.IsAdministrator) != true) throw new AiException("Administrator access required.", 403);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var result = await client.Complete(CurrentOptions(),
            [new { role = "user", content = "Call the connection_probe function now with no arguments. Do not respond with prose." }],
            [new { type = "function", function = new { name = "connection_probe", description = "Test function calling without accessing any user data.", parameters = new { type = "object", properties = new { }, additionalProperties = false } } }], deadline.Token).ConfigureAwait(false);
        if (!result.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != System.Text.Json.JsonValueKind.Array || calls.GetArrayLength() != 1 ||
            calls[0].ValueKind != System.Text.Json.JsonValueKind.Object || !calls[0].TryGetProperty("function", out var function) ||
            function.ValueKind != System.Text.Json.JsonValueKind.Object || !function.TryGetProperty("name", out var name) ||
            name.ValueKind != System.Text.Json.JsonValueKind.String || name.GetString() != "connection_probe")
            throw new AiException("Endpoint responded, but the model did not produce the required tool call. Choose a tool-capable model.");
        return new { Ready = true };
    });

    private static AiThread Find(AiUserData data, Guid id) => data.Threads.SingleOrDefault(t => t.Id == id) ?? throw new AiException("Conversation not found.", 404);
    private AiOptions CurrentOptions() => AiOptions.From(Configuration) ?? throw new AiException("The assistant is disabled or not configured.", 404);
}
