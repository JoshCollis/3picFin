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
    Func<Guid, string, CancellationToken, Task<AiCard?>>? resolve = null, Func<bool>? sharedEnabled = null, AiLiveTv? liveTv = null)
{
    private static object Define(string name, string description, string properties, string required = "[]") =>
        new { type = "function", function = new { name, description, parameters = JsonSerializer.Deserialize<JsonElement>(
            "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":" + properties + ",\"required\":" + required + "}") } };

    public static readonly object[] Definitions =
    [
        Define("search_library", "Search ALL accessible Jellyfin movie/show libraries, not Home rows. Empty query browses. Exact genre, actor/director name (person), release year, favourites and watched filters can combine. sort=recent means recently ADDED, not newly released; rating is descending community score. maxMinutes filters each page; continue even empty pages when nextPage exists. Prefer unwatched for recommendations unless user wants a rewatch.",
            """{"query":{"type":"string"},"genre":{"type":"string"},"person":{"type":"string"},"year":{"type":"integer","minimum":1800,"maximum":2100},"favorite":{"type":"boolean"},"sort":{"type":"string","enum":["name","recent","rating"]},"type":{"type":"string","enum":["all","movie","tv"]},"watched":{"type":"boolean"},"maxMinutes":{"type":"integer","minimum":1,"maximum":600},"page":{"type":"integer","minimum":1,"maximum":1000000}}"""),
        Define("get_viewing_context", "Refresh this viewer's own recent movies/series, favourites and in-progress titles, with verified title keys usable as recommendation seeds. Only when personalization is enabled. Recent series means at least one watched episode, not whole-series completion. A sample, not exhaustive history.", "{}"),
        Define("get_library_episodes", "Check exact episodes held in the viewer's visible Jellyfin library for a verified series key. Optional season (0 means specials); paginate before claiming completeness. Seerr season eligibility is NOT library inventory. Use show_titles with the verified series for a clickable card.",
            """{"key":{"type":"string"},"season":{"type":"integer","minimum":0,"maximum":1000},"page":{"type":"integer","minimum":1,"maximum":1000000}}""", "[\"key\"]"),
        Define("find_related_titles", "Get live Seerr/TMDb recommendations or similar titles based on a verified get_title/search_library seed (including favourites/activity keys). Results can be outside the library. Verify picks with get_title and explain why they fit; similarity is a lead, not a guarantee. Use mode=similar if recommendations are empty.",
            """{"key":{"type":"string"},"mode":{"type":"string","enum":["recommendations","similar"]},"page":{"type":"integer","minimum":1,"maximum":100}}""", "[\"key\"]"),
        Define("get_my_requests", "Read this viewer's own Seerr requests and status, never other users. Use get_title on returned title keys for names and Jellyfin availability. Completed in Seerr does not prove playback availability. Read-only, paginated.",
            """{"page":{"type":"integer","minimum":1,"maximum":100}}"""),
        Define("search_catalog", "Search Seerr/TMDb for movies and shows including titles NOT owned. Search specific title names, not conversational sentences. Results are candidates, not proof of local availability.",
            """{"query":{"type":"string"},"page":{"type":"integer","minimum":1,"maximum":100}}""", "[\"query\"]"),
        Define("browse_catalog", "Live Seerr/TMDb discovery beyond the library/model knowledge: trending, popular movies/tv, recent (last 180 days) or upcoming releases. Verify candidates with get_title. Optional inclusive releasedAfter/releasedBefore are yyyy-MM-dd primary movie release/TV FIRST air dates, not episode schedules or streaming availability. Optional exact TMDb genre name: e.g. Comedy, Horror (movie), Science Fiction (movie), Sci-Fi & Fantasy (tv), Action & Adventure (tv). Filters require a movie/TV feed, not trending.",
            """{"feed":{"type":"string","enum":["trending","movies","tv","recent_movies","recent_tv","upcoming_movies","upcoming_tv"]},"genre":{"type":"string"},"releasedAfter":{"type":"string"},"releasedBefore":{"type":"string"},"page":{"type":"integer","minimum":1,"maximum":100}}""", "[\"feed\"]"),
        Define("list_live_tv_channels", "Search the viewer's entire Live TV channel list by name OR number; empty query lists channels. Paginated, independent of Home rows. Use returned channelId with search_live_tv_guide; never invent channel IDs or stream URLs.",
            """{"query":{"type":"string"},"page":{"type":"integer","minimum":1,"maximum":1000000}}"""),
        Define("search_live_tv_guide", "Search Jellyfin's imported EPG across all permitted channels. when=now for what's on; upcoming for current/future listings through the end of the imported guide; window requires start/end ISO-8601 timestamps WITH Z or UTC offset (overlapping programmes included). query is a title search, not a sentence. Optional channelId from list_live_tv_channels; type filters movies/shows/sports/news/kids. Always report channel name/number, date and time zone. A missing listing does not prove it will not air. Continue pages or refine. Read-only: cannot record or set reminders.",
            """{"when":{"type":"string","enum":["now","upcoming","window"]},"query":{"type":"string"},"channelId":{"type":"string"},"start":{"type":"string"},"end":{"type":"string"},"type":{"type":"string","enum":["all","movie","tv","sports","news","kids"]},"page":{"type":"integer","minimum":1,"maximum":1000000}}""", "[\"when\"]"),
        Define("get_title", "Verify a title/channel and local availability. key is movie:TMDB_ID, tv:TMDB_ID, library:JELLYFIN_GUID or channel:CHANNEL_GUID from the Live TV tools. Use get_title then show_titles for clickable channel cards too.",
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

    private bool Shared => sharedEnabled?.Invoke() ?? Plugin.Current?.Configuration.SharedRequestsEnabled == true;
    public object Context(Guid user) => new { catalogConfigured = seerr?.IsConfigured == true, catalogGenres = seerr?.IsConfigured == true ? AiCatalog.GenreNames : null,
        liveTvAvailable = liveTv?.IsAvailable(user) == true, householdRequestsEnabled = Shared,
        note = "Configured does not guarantee upstream connectivity or Seerr account mapping. No unrestricted web browsing, recording or background reminders." };

    public object[] AvailableDefinitions(Guid user, AiUserData data) => Definitions.Where(definition =>
    {
        var name = JsonSerializer.SerializeToElement(definition).GetProperty("function").GetProperty("name").GetString();
        return name switch
        {
            "search_catalog" or "browse_catalog" or "find_related_titles" or "get_my_requests" or "prepare_request" => seerr?.IsConfigured == true,
            "requesters" => seerr?.IsConfigured == true && Shared,
            "list_live_tv_channels" or "search_live_tv_guide" => liveTv?.IsAvailable(user) == true,
            "get_viewing_context" => data.Personalization,
            "remember_taste" => data.LearnMemory, _ => true
        };
    }).ToArray();

    public sealed class Turn(Guid user, AiUserData data, AiMessage response, Func<bool> authorized, TimeZoneInfo? timeZone = null)
    {
        public Guid User { get; } = user;
        public AiUserData Data { get; } = data;
        public AiMessage Response { get; } = response;
        public TimeZoneInfo TimeZone { get; } = timeZone ?? TimeZoneInfo.Utc;
        public Dictionary<string, AiCard> Verified { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RequestOptionsResult> Options { get; } = new(StringComparer.Ordinal);
        public void Check() { if (!authorized()) throw new AiException("The assistant is no longer available for this account.", 403); }
    }

    public async Task<object> Run(Turn turn, string name, JsonElement args, CancellationToken token)
    {
        turn.Check();
        var allowed = name switch
        {
            "search_library" => new[] { "query", "genre", "type", "watched", "page", "maxMinutes", "sort", "person", "year", "favorite" },
            "get_viewing_context" => [], "find_related_titles" => ["key", "mode", "page"], "get_my_requests" => ["page"],
            "get_library_episodes" => ["key", "season", "page"],
            "search_catalog" => ["query", "page"], "browse_catalog" => ["feed", "page", "genre", "releasedAfter", "releasedBefore"], "get_title" or "requesters" => ["key"],
            "list_live_tv_channels" => ["query", "page"], "search_live_tv_guide" => ["when", "query", "channelId", "start", "end", "type", "page"],
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
                    var sort = Text(args, "sort", 10, "name");
                    if (sort is not ("name" or "recent" or "rating")) throw new FormatException();
                    int? year = args.TryGetProperty("year", out _) ? Integer(args, "year", 2000, 2100) : null;
                    if (year < 1800) throw new FormatException();
                    var result = library.Search(turn.User, Text(args, "query", 200, ""), Text(args, "genre", 80, ""), type,
                        args.TryGetProperty("watched", out var watched) ? watched.GetBoolean() : null, Integer(args, "page", 1, 1_000_000),
                        args.TryGetProperty("maxMinutes", out _) ? Integer(args, "maxMinutes", 120, 600) : null,
                        sort, Text(args, "person", 120, ""), year, args.TryGetProperty("favorite", out var favorite) ? favorite.GetBoolean() : null);
                    // Re-read every selected card later too; no model-provided library IDs become trusted links.
                    using (var doc = JsonDocument.Parse(AiJson.Write(result)))
                        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                        {
                            var card = item.Deserialize<AiCard>(AiJson.Options)!;
                            turn.Verified[card.Key] = card;
                        }
                    return result;
                case "get_viewing_context":
                    if (!turn.Data.Personalization) return new { error = "Viewing personalization is disabled." };
                    var activity = library.Personalize(turn.User);
                    turn.Check();
                    return activity;
                case "get_library_episodes":
                    var series = await Get(turn, Text(args, "key", 80), token).ConfigureAwait(false);
                    if (series is not { MediaType: "tv", LibraryItemId: { } seriesId }) return new { error = "Resolve a series in your accessible library first." };
                    int? season = args.TryGetProperty("season", out var number) ? number.GetInt32() : null;
                    if (season is < 0 or > 1000) throw new FormatException();
                    var inventory = library.Episodes(turn.User, seriesId, season, Integer(args, "page", 1, 1_000_000));
                    turn.Check(); return inventory;
                case "find_related_titles":
                    var seedKey = Text(args, "key", 80);
                    var seed = await Get(turn, seedKey, token).ConfigureAwait(false);
                    if (seed?.TmdbId is not > 0 || seed.MediaType is not ("movie" or "tv")) return new { error = "Resolve a movie/show with a TMDb identity first." };
                    var mode = Text(args, "mode", 20, "recommendations");
                    if (mode is not ("recommendations" or "similar")) throw new FormatException();
                    var relatedPage = Integer(args, "page", 1, 100);
                    var related = await seerr.GetRelatedTitlesAsync(turn.User, seed.MediaType, seed.TmdbId.Value, mode, relatedPage, token).ConfigureAwait(false);
                    turn.Check();
                    return Catalog(related, relatedPage, seed.MediaType, new { seed = seed.Key, mode });
                case "get_my_requests":
                    var ownPage = Integer(args, "page", 1, 100);
                    var own = DiscoveryDtos.Requests(await seerr.GetOwnRequestsAsync(turn.User, ownPage, token).ConfigureAwait(false));
                    turn.Check();
                    return new { source = "Seerr own requests", requests = own.Items.Select(r => new
                        { key = r.TmdbId.HasValue ? (r.MediaType ?? r.Type) + ":" + r.TmdbId : null, r.Id, r.Status, r.Is4k, r.Seasons }),
                        error = own.Error, page = ownPage, nextPage = own.Error is null && ownPage < 100 && (own.TotalPages is null || ownPage < own.TotalPages) ? ownPage + 1 : (int?)null,
                        complete = own.Error is null && own.TotalPages.HasValue && ownPage >= own.TotalPages };
                case "search_catalog":
                    var query = Text(args, "query", 200);
                    var page = Integer(args, "page", 1, 100);
                    var source = await seerr.GetUserReadAsync(turn.User, $"api/v1/search?query={Uri.EscapeDataString(query)}&page={page}", token).ConfigureAwait(false);
                    turn.Check();
                    return Catalog(source, page);
                case "browse_catalog":
                    var feed = Text(args, "feed", 20);
                    var catalogPage = Integer(args, "page", 1, 100);
                    var browse = AiCatalog.Browse(feed, catalogPage, Text(args, "genre", 80, ""),
                        args.TryGetProperty("releasedAfter", out _) ? Text(args, "releasedAfter", 10) : null,
                        args.TryGetProperty("releasedBefore", out _) ? Text(args, "releasedBefore", 10) : null, DateOnly.FromDateTime(DateTime.UtcNow));
                    var catalog = await seerr.GetUserReadAsync(turn.User, browse.Path, token).ConfigureAwait(false);
                    turn.Check();
                    return Catalog(catalog, catalogPage, browse.Type, new { feed, genre = Text(args, "genre", 80, ""), releasedAfter = browse.From, releasedBefore = browse.To });
                case "list_live_tv_channels":
                case "search_live_tv_guide":
                    if (liveTv is null) return new { error = "Live TV guide unavailable.", complete = false };
                    object guide;
                    if (name == "list_live_tv_channels") guide = liveTv.Channels(turn.User, Text(args, "query", 200, ""), Integer(args, "page", 1, 1_000_000), token);
                    else
                    {
                        var when = Text(args, "when", 10);
                        var guideType = Text(args, "type", 10, "all");
                        if (when is not ("now" or "upcoming" or "window") || guideType is not ("all" or "movie" or "tv" or "sports" or "news" or "kids")) throw new FormatException();
                        Guid? channel = args.TryGetProperty("channelId", out _) ? Guid.Parse(Text(args, "channelId", 36)) : null;
                        if (channel == Guid.Empty) throw new FormatException();
                        var start = AiLiveTv.Date(args.TryGetProperty("start", out _) ? Text(args, "start", 40) : null);
                        var end = AiLiveTv.Date(args.TryGetProperty("end", out _) ? Text(args, "end", 40) : null);
                        if (when == "window" ? start is null || end is null || end <= start : start.HasValue || end.HasValue) throw new FormatException();
                        guide = liveTv.Guide(turn.User, Text(args, "query", 200, ""), channel, when, start, end, guideType,
                            Integer(args, "page", 1, 1_000_000), turn.TimeZone, token);
                    }
                    turn.Check();
                    return guide;
                case "get_title":
                    var verified = await Get(turn, Text(args, "key", 80), token).ConfigureAwait(false);
                    if (verified is null) return new { error = "Title unavailable or not visible." };
                    var options = turn.Options.TryGetValue(verified.Key, out var freshOptions) ? freshOptions :
                        verified.TmdbId is > 0 ? await seerr.GetRequestOptionsAsync(turn.User, verified.MediaType, verified.TmdbId.Value, token).ConfigureAwait(false) : null;
                    turn.Check();
                    return new { title = verified, requestOptions = options?.Value, optionsError = options?.Failure?.ToString() };
                case "show_titles":
                    var keys = args.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).Distinct(StringComparer.Ordinal).ToArray();
                    if (keys.Length is < 1 or > 5 || turn.Response.Cards.Select(c => c.Key).Concat(keys!).Distinct().Count() > 5) throw new FormatException();
                    var cards = new List<AiCard>();
                    foreach (var key in keys)
                    {
                        if (key is null || !turn.Verified.ContainsKey(key)) return new { error = "Verify each title using get_title first." };
                        var card = await Get(turn, key, token).ConfigureAwait(false);
                        if (card is null) return new { error = "One of the selected titles/channels is no longer available. Recheck your picks." };
                        if (!turn.Response.Cards.Any(c => c.Key == card.Key)) cards.Add(card);
                    }
                    turn.Response.Cards.AddRange(cards.DistinctBy(c => c.Key));
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
        if (parts[0] == "channel" && Guid.TryParse(parts[1], out var channelId)) card = liveTv?.GetChannel(turn.User, channelId);
        else if (parts[0] == "library" && Guid.TryParse(parts[1], out var itemId)) card = library.Get(turn.User, itemId);
        else if (parts[0] is "movie" or "tv" && int.TryParse(parts[1], out var tmdb) && tmdb is > 0 and <= 100_000_000)
        {
            var result = await seerr.GetTitleDetailAsync(turn.User, parts[0], tmdb, token).ConfigureAwait(false);
            turn.Check();
            if (result.Value is not { } detail || detail.MediaStatus == 6) return null;
            if (result.RequestOptions is not null) turn.Options[key] = result.RequestOptions;
            var local = TitleDetailsController.ResolveLibrary(users, libraries, turn.User, parts[0], tmdb, result.JellyfinHint);
            card = new(key, detail.Title, parts[0], tmdb, local.ItemId, detail.Date, detail.PosterPath, AiJson.Clip(detail.Overview, 600),
                detail.RuntimeMinutes, detail.Genres, CommunityRating: detail.CommunityRating);
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

    private static object Catalog(SeerrResult source, int page, string? type = null, object? filters = null)
    {
        if (source.Failure is not null) return new { error = "Catalog unavailable or account not mapped to Seerr." };
        var root = source.Value!.Value;
        var safeRows = root.GetProperty("results").EnumerateArray().Where(row =>
            !row.TryGetProperty("adult", out var adult) || adult.ValueKind == JsonValueKind.False).ToArray();
        var safeSource = SeerrResult.Success(JsonSerializer.SerializeToElement(new { results = safeRows }));
        int? pages = root.TryGetProperty("totalPages", out var total) && total.TryGetInt32(out var count) && count is >= 0 and <= 10000 ? count : null;
        return new
        {
            source = "Seerr/TMDb", retrievedAt = DateTimeOffset.UtcNow, filters,
            items = DiscoveryDtos.Catalog(safeSource, type).Items.Where(c => c.Status != 6).Select(c => new
            { key = c.MediaType + ":" + c.TmdbId, c.Title, c.Date, c.MediaType, c.Overview }),
            page, totalPages = pages, nextPage = page < 100 && (pages is null || page < pages) ? page + 1 : (int?)null,
            complete = pages.HasValue && page >= pages
        };
    }
}
