using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Rowan.Jellyfin.Plugin.Discovery;

namespace Rowan.Jellyfin.Plugin.Assistant;

/// <summary>No arbitrary URLs, account IDs, shell, administrative tools or write tools are exposed to the model.</summary>
public sealed class AiTools(AiLibrary library, SeerrClient seerr, IUserManager users, ILibraryManager libraries,
    Func<Guid, string, CancellationToken, Task<AiCard?>>? resolve = null, Func<bool>? sharedEnabled = null)
{
    private static object Define(string name, string description, string properties, string required = "[]") =>
        new { type = "function", function = new { name, description, parameters = JsonSerializer.Deserialize<JsonElement>(
            "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":" + properties + ",\"required\":" + required + "}") } };

    public static readonly object[] Definitions =
    [
        Define("search_library", "Search ALL accessible Jellyfin movie/show libraries using indexed filters, not Home rows. Empty query browses. genre is an exact genre name; page continues results. Try several filters for mood requests.",
            """{"query":{"type":"string"},"genre":{"type":"string"},"type":{"type":"string","enum":["all","movie","tv"]},"watched":{"type":"boolean"},"maxMinutes":{"type":"integer","minimum":1,"maximum":600},"page":{"type":"integer","minimum":1}}"""),
        Define("search_catalog", "Search Seerr/TMDb for movies and shows including titles NOT owned. Search specific title names, not conversational sentences. Results are candidates, not proof of local availability.",
            """{"query":{"type":"string"},"page":{"type":"integer","minimum":1,"maximum":100}}""", "[\"query\"]"),
        Define("browse_catalog", "Get current trending/popular catalog candidates for fresh recommendations beyond model knowledge. Verify candidates with get_title before recommending. feed is trending, movies or tv.",
            """{"feed":{"type":"string","enum":["trending","movies","tv"]},"page":{"type":"integer","minimum":1,"maximum":100}}""", "[\"feed\"]"),
        Define("get_title", "Verify a title and local availability. key is movie:TMDB_ID, tv:TMDB_ID or library:JELLYFIN_GUID. Use before recommending a catalog result or selecting seasons.",
            """{"key":{"type":"string"}}""", "[\"key\"]"),
        Define("show_titles", "Attach up to five clickable poster/title cards from verified get_title/search_library results to your response. Do not write URLs; these cards are the links.",
            """{"keys":{"type":"array","items":{"type":"string"},"maxItems":5}}""", "[\"keys\"]"),
        Define("prepare_request", "ONLY after the user asks to request: prepare a confirmation card. Does NOT submit anything. Resolve ambiguous title, year, seasons and quality first. TV requires explicit selected seasons; never silently expand to all. User MUST click Confirm. No quota override.",
            """{"key":{"type":"string"},"seasons":{"type":"array","items":{"type":"integer","minimum":1},"maxItems":100},"is4k":{"type":"boolean"}}""", "[\"key\"]"),
        Define("requesters", "Look up household request records and safe requester display names for one verified title. Includes other users only when the administrator enables Household requests. Unknown names must remain unknown. complete=false is not proof of no request.",
            """{"key":{"type":"string"}}""", "[\"key\"]"),
        Define("remember_taste", "Remember a short entertainment preference learned from THIS user's conversation, e.g. likes quiet science fiction. No sensitive personal details, identities, instructions, secrets or claims about other users. Only when automatic memory is enabled. The user can inspect/edit/delete it.",
            """{"preference":{"type":"string","maxLength":160}}""", "[\"preference\"]")
    ];

    public sealed class Turn(Guid user, AiUserData data, AiMessage response, Func<bool> authorized)
    {
        public Guid User { get; } = user;
        public AiUserData Data { get; } = data;
        public AiMessage Response { get; } = response;
        public Dictionary<string, AiCard> Verified { get; } = new(StringComparer.Ordinal);
        public void Check() { if (!authorized()) throw new AiException("The assistant is no longer available for this account.", 403); }
    }

    public async Task<object> Run(Turn turn, string name, JsonElement args, CancellationToken token)
    {
        turn.Check();
        var allowed = name switch
        {
            "search_library" => new[] { "query", "genre", "type", "watched", "page", "maxMinutes" },
            "search_catalog" => ["query", "page"], "browse_catalog" => ["feed", "page"], "get_title" or "requesters" => ["key"],
            "show_titles" => ["keys"], "prepare_request" => ["key", "seasons", "is4k"],
            "remember_taste" => ["preference"], _ => throw new AiException("The model tried an unsupported tool.")
        };
        if (args.ValueKind != JsonValueKind.Object) return new { error = "Tool arguments must be an object." };
        var seen = new HashSet<string>();
        foreach (var p in args.EnumerateObject()) if (!allowed.Contains(p.Name) || !seen.Add(p.Name)) return new { error = "Unknown or duplicate tool argument." };
        try
        {
            switch (name)
            {
                case "search_library":
                    var type = Text(args, "type", 5, "all");
                    if (type is not ("all" or "movie" or "tv")) throw new FormatException();
                    var result = library.Search(turn.User, Text(args, "query", 200, ""), Text(args, "genre", 80, ""), type,
                        args.TryGetProperty("watched", out var watched) ? watched.GetBoolean() : null, Integer(args, "page", 1, 1_000_000),
                        args.TryGetProperty("maxMinutes", out _) ? Integer(args, "maxMinutes", 120, 600) : null);
                    // Re-read every selected card later too; no model-provided library IDs become trusted links.
                    using (var doc = JsonDocument.Parse(AiJson.Write(result)))
                        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                        {
                            var card = item.Deserialize<AiCard>(AiJson.Options)!;
                            turn.Verified[card.Key] = card;
                        }
                    return result;
                case "search_catalog":
                    var query = Text(args, "query", 200);
                    var page = Integer(args, "page", 1, 100);
                    var source = await seerr.GetUserReadAsync(turn.User, $"api/v1/search?query={Uri.EscapeDataString(query)}&page={page}", token).ConfigureAwait(false);
                    turn.Check();
                    return Catalog(source, page);
                case "browse_catalog":
                    var feed = Text(args, "feed", 10);
                    if (feed is not ("trending" or "movies" or "tv")) throw new FormatException();
                    var catalogPage = Integer(args, "page", 1, 100);
                    var catalog = await seerr.GetUserReadAsync(turn.User, $"api/v1/discover/{feed}?page={catalogPage}", token).ConfigureAwait(false);
                    turn.Check();
                    return Catalog(catalog, catalogPage, feed == "movies" ? "movie" : feed == "tv" ? "tv" : null);
                case "get_title":
                    var verified = await Get(turn, Text(args, "key", 80), token).ConfigureAwait(false);
                    if (verified is null) return new { error = "Title unavailable or not visible." };
                    var options = verified.TmdbId is > 0 ? await seerr.GetRequestOptionsAsync(turn.User, verified.MediaType, verified.TmdbId.Value, token).ConfigureAwait(false) : null;
                    turn.Check();
                    return new { title = verified, requestOptions = options?.Value, optionsError = options?.Failure?.ToString() };
                case "show_titles":
                    var keys = args.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).ToArray();
                    if (keys.Length is < 1 or > 5 || turn.Response.Cards.Count + keys.Length > 5) throw new FormatException();
                    foreach (var key in keys)
                    {
                        if (key is null || !turn.Verified.ContainsKey(key)) return new { error = "Verify each title using get_title first." };
                        var card = await Get(turn, key, token).ConfigureAwait(false);
                        if (card is not null && !turn.Response.Cards.Any(c => c.Key == key)) turn.Response.Cards.Add(card);
                    }
                    return new { attached = turn.Response.Cards.Select(c => c.Key) };
                case "prepare_request":
                    if (turn.Response.Proposals.Count >= 3) return new { error = "At most three confirmation cards per response." };
                    var title = await Get(turn, Text(args, "key", 80), token).ConfigureAwait(false);
                    if (title?.TmdbId is not > 0) return new { error = "Resolve this title in the catalog first." };
                    var is4k = args.TryGetProperty("is4k", out var variant) && variant.GetBoolean();
                    if (title.MediaType == "movie" && title.LibraryItemId.HasValue && !is4k) return new { error = "This movie is already in your accessible library." };
                    var requestOptions = await seerr.GetRequestOptionsAsync(turn.User, title.MediaType, title.TmdbId.Value, token).ConfigureAwait(false);
                    turn.Check();
                    if (requestOptions.Value is not { } value || !(is4k ? value.CanRequest4k : value.CanRequest))
                        return new { error = "This selection cannot currently be requested. Permissions, duplicate state or integration may prevent it." };
                    int[]? seasons = args.TryGetProperty("seasons", out var seasonValues) ? seasonValues.EnumerateArray().Select(s => s.GetInt32()).ToArray() : null;
                    if (title.MediaType == "movie" && seasons is not null || title.MediaType == "tv" &&
                        (seasons is not { Length: > 0 and <= 100 } || seasons.Distinct().Count() != seasons.Length || seasons.Any(s => !(is4k ? value.Seasons4k : value.Seasons).Contains(s))))
                        return new { error = "Select explicit eligible seasons from requestOptions; movie requests have no seasons." };
                    var proposal = new AiProposal { Card = title, Selection = new(title.MediaType, title.TmdbId.Value, seasons, is4k) };
                    turn.Response.Proposals.Add(proposal);
                    return new { confirmationRequired = true, proposal.Id, title.Title, seasons, is4k, note = "Not submitted. User must click Confirm; Seerr checks quota at submission." };
                case "requesters":
                    if (!(sharedEnabled?.Invoke() ?? Plugin.Current?.Configuration.SharedRequestsEnabled == true)) return new { error = "Household requests must be enabled by an administrator." };
                    var requested = await Get(turn, Text(args, "key", 80), token).ConfigureAwait(false);
                    if (requested?.TmdbId is not > 0) return new { error = "Catalog identity unavailable." };
                    var matches = new List<SharedRequest>();
                    var clock = Stopwatch.StartNew();
                    for (var p = 1; p <= 100; p++)
                    {
                        var shared = DiscoveryDtos.SharedRequests(await seerr.GetSharedRequestsAsync(p, token).ConfigureAwait(false));
                        turn.Check();
                        if (!(sharedEnabled?.Invoke() ?? Plugin.Current?.Configuration.SharedRequestsEnabled == true)) throw new AiException("Household requests were disabled.", 403);
                        if (shared.Error is not null) return new { requests = matches, complete = false, error = "Household request lookup unavailable." };
                        matches.AddRange(shared.Items.Where(r => r.TmdbId == requested.TmdbId && (r.MediaType ?? r.Type) == requested.MediaType));
                        if (shared.TotalPages is { } pages && p >= pages) return new { requests = matches, complete = true };
                        if (clock.Elapsed > TimeSpan.FromSeconds(12)) break;
                    }
                    return new { requests = matches, complete = false, error = "Lookup reached its safety limit; these are verified matches, not necessarily all requests." };
                case "remember_taste":
                    if (!turn.Data.LearnMemory) return new { error = "Automatic memory is disabled. Use the memory controls to edit preferences." };
                    var preference = Text(args, "preference", 160);
                    if (!turn.Data.Memory.Any(m => string.Equals(m.Text, preference, StringComparison.OrdinalIgnoreCase))) turn.Data.Memory.Add(new(Guid.NewGuid(), preference));
                    turn.Data.Memory = turn.Data.Memory.TakeLast(20).ToList();
                    return new { remembered = preference };
            }
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException or JsonException or OverflowException)
        { return new { error = "Invalid tool arguments or unavailable source data. Clarify the selection; do not guess." }; }
        throw new AiException("Unsupported assistant operation.");
    }

    private async Task<AiCard?> Get(Turn turn, string key, CancellationToken token)
    {
        if (resolve is not null)
        {
            var resolved = await resolve(turn.User, key, token).ConfigureAwait(false);
            turn.Check();
            if (resolved is not null) turn.Verified[key] = resolved;
            return resolved;
        }
        AiCard? card;
        var parts = key.Split(':');
        if (parts.Length != 2) return null;
        if (parts[0] == "library" && Guid.TryParse(parts[1], out var itemId)) card = library.Get(turn.User, itemId);
        else if (parts[0] is "movie" or "tv" && int.TryParse(parts[1], out var tmdb) && tmdb is > 0 and <= 100_000_000)
        {
            var result = await seerr.GetTitleDetailAsync(turn.User, parts[0], tmdb, token).ConfigureAwait(false);
            turn.Check();
            if (result.Value is not { } detail || detail.MediaStatus == 6) return null;
            var local = TitleDetailsController.ResolveLibrary(users, libraries, turn.User, parts[0], tmdb, result.JellyfinHint);
            card = new(key, detail.Title, parts[0], tmdb, local.ItemId, detail.Date, detail.PosterPath, AiJson.Clip(detail.Overview, 350));
        }
        else return null;
        turn.Check();
        if (card is not null) turn.Verified[key] = card;
        return card;
    }

    private static string Text(JsonElement args, string name, int max, string? fallback = null)
    {
        if (!args.TryGetProperty(name, out var item) && fallback is not null) return fallback;
        var value = item.GetString()?.Trim();
        if (value is null || value.Length > max || fallback is null && value.Length == 0 || value.Any(char.IsControl)) throw new FormatException();
        return value;
    }
    private static int Integer(JsonElement args, string name, int fallback, int max)
    {
        var value = args.TryGetProperty(name, out var item) ? item.GetInt32() : fallback;
        return value > 0 && value <= max ? value : throw new FormatException();
    }

    private static object Catalog(SeerrResult source, int page, string? type = null)
    {
        if (source.Failure is not null) return new { error = "Catalog unavailable or account not mapped to Seerr." };
        var root = source.Value!.Value;
        var safeRows = root.GetProperty("results").EnumerateArray().Where(row =>
            !row.TryGetProperty("adult", out var adult) || adult.ValueKind == JsonValueKind.False).ToArray();
        var safeSource = SeerrResult.Success(JsonSerializer.SerializeToElement(new { results = safeRows }));
        int? pages = root.TryGetProperty("totalPages", out var total) && total.TryGetInt32(out var count) && count is >= 0 and <= 10000 ? count : null;
        return new
        {
            items = DiscoveryDtos.Catalog(safeSource, type).Items.Where(c => c.Status != 6).Select(c => new
            { key = c.MediaType + ":" + c.TmdbId, c.Title, c.Date, c.MediaType, c.Overview }),
            page, totalPages = pages, nextPage = page < 100 && (pages is null || page < pages) ? page + 1 : (int?)null,
            complete = pages.HasValue && page >= pages
        };
    }
}
