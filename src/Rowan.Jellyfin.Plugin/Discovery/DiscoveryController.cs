using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Rowan.Jellyfin.Plugin.Home;
using Rowan.Jellyfin.Plugin.Configuration;

namespace Rowan.Jellyfin.Plugin.Discovery;

public sealed record CatalogItem(int TmdbId, string MediaType, string Title, string? Date, string? PosterPath, string? Overview, int? Status);
public sealed record PersonalRequest(int Id, int Status, string? Type, int? TmdbId, string? MediaType, int? MediaStatus, bool? Is4k, IReadOnlyList<int> Seasons, string? Title, string? PosterPath);
public sealed record SharedRequest(int Id, int Status, string? Type, int? TmdbId, string? MediaType, int? MediaStatus, bool? Is4k, IReadOnlyList<int> Seasons);
public sealed record SourceResult<T>(IReadOnlyList<T> Items, string? Error, int? Page = null, int? TotalPages = null, int? TotalResults = null);
public sealed record DiscoveryResponse(SourceResult<CatalogItem> Movies, SourceResult<CatalogItem> Tv, SourceResult<PersonalRequest> Requests);

/// <summary>Allowlisted, bounded conversion from upstream JSON. Never serialize SeerrResult.Value.</summary>
public static class DiscoveryDtos
{
    public static SourceResult<SharedRequest> SharedRequests(SeerrResult source)
    {
        var personal = Requests(source);
        var items = new List<SharedRequest>();
        foreach (var item in personal.Items)
            items.Add(new(item.Id, item.Status, item.Type, item.TmdbId, item.MediaType, item.MediaStatus, item.Is4k, item.Seasons));
        return new(items, personal.Error, personal.Page, personal.TotalPages, personal.TotalResults);
    }

    public static SourceResult<CatalogItem> Catalog(SeerrResult source, string? expectedType = null)
    {
        if (source.Failure is { } failure) return new([], failure.ToString());
        if (!TryResults(source.Value, out var results)) return new([], SeerrFailure.UpstreamUnavailable.ToString());
        var items = new List<CatalogItem>();
        foreach (var item in results.EnumerateArray())
        {
            if (items.Count >= 20) break;
            var type = Text(item, "mediaType", 12) ?? expectedType;
            if (type is not ("movie" or "tv") || (expectedType is not null && type != expectedType)) continue;
            var id = Number(item, "id");
            if (id is not > 0) continue;
            var title = Text(item, type == "tv" ? "name" : "title", 200);
            if (string.IsNullOrWhiteSpace(title)) continue;
            var date = Text(item, type == "tv" ? "firstAirDate" : "releaseDate", 10);
            var poster = Text(item, "posterPath", 200);
            // Only TMDb-style relative poster paths, not arbitrary upstream URLs or filesystem paths.
            if (poster is not null && (!poster.StartsWith('/') || poster.StartsWith("//") || poster.Contains("..") || poster.Contains('\\'))) poster = null;
            int? status = null;
            if (item.TryGetProperty("mediaInfo", out var info) && info.ValueKind == JsonValueKind.Object) status = Number(info, "status");
            items.Add(new(id.Value, type, title, date, poster, Text(item, "overview", 500), status));
        }
        var root = source.Value!.Value;
        return new(items, null, Bounded(Number(root, "page"), 1, 100), Bounded(Number(root, "totalPages"), 0, 10_000), Bounded(Number(root, "totalResults"), 0, 1_000_000));
    }

    public static SourceResult<PersonalRequest> Requests(SeerrResult source)
    {
        if (source.Failure is { } failure) return new([], failure.ToString());
        if (!TryResults(source.Value, out var results)) return new([], SeerrFailure.UpstreamUnavailable.ToString());
        var items = new List<PersonalRequest>();
        foreach (var item in results.EnumerateArray())
        {
            if (items.Count >= 20) break;
            var id = Number(item, "id");
            var status = Bounded(Number(item, "status"), 0, 100);
            if (id is not > 0 || status is null) continue;
            var media = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("media", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : default;
            var seasons = new List<int>();
            if (item.TryGetProperty("seasons", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var season in array.EnumerateArray())
                {
                    if (seasons.Count >= 100) break;
                    if (Bounded(Number(season, "seasonNumber"), 0, 1000) is { } number) seasons.Add(number);
                }
            items.Add(new PersonalRequest(id.Value, status.Value, MediaType(item, "type"), Bounded(Number(media, "tmdbId"), 1, 100_000_000),
                MediaType(media, "mediaType"), Bounded(Number(media, "status"), 0, 100), Boolean(item, "is4k"), seasons, null, null));
        }
        var root = source.Value!.Value;
        var pageInfo = root.TryGetProperty("pageInfo", out var info) && info.ValueKind == JsonValueKind.Object ? info : default;
        return new(items, null, Bounded(Number(pageInfo, "page"), 1, 100), Bounded(Number(pageInfo, "pages"), 0, 10_000), Bounded(Number(pageInfo, "results"), 0, 1_000_000));
    }

    private static bool TryResults(JsonElement? value, out JsonElement results)
    {
        results = default;
        return value is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("results", out results) && results.ValueKind == JsonValueKind.Array;
    }
    private static int? Number(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static int? Bounded(int? value, int min, int max) => value is { } n && n >= min && n <= max ? n : null;
    private static string? MediaType(JsonElement item, string key) => Text(item, key, 12) is { } type && type is "movie" or "tv" ? type : null;
    private static bool? Boolean(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
    private static string? Text(JsonElement item, string key, int limit) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text ? text[..Math.Min(text.Length, limit)] : null;
}

[ApiController]
[Route("3picFin")]
[Authorize]
public sealed class DiscoveryController : ControllerBase
{
    private readonly SeerrClient _client;
    private readonly Func<Guid, bool> _userExists;
    private readonly Func<bool> _sharedEnabled;

    [ActivatorUtilitiesConstructor]
    public DiscoveryController(SeerrClient client, IUserManager users) : this(client, id => users.GetUserById(id) is not null,
        () => Plugin.Current?.Configuration.SharedRequestsEnabled == true) { }

    /// <summary>Allows isolated controller tests without a live Jellyfin database.</summary>
    public DiscoveryController(SeerrClient client, Func<Guid, bool> userExists, Func<bool>? sharedEnabled = null)
    {
        _client = client;
        _userExists = userExists;
        _sharedEnabled = sharedEnabled ?? (() => false);
    }

    [HttpGet("SharedRequests")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SourceResult<SharedRequest>>> GetSharedRequests([FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out _)) return Forbid();
        if (!_sharedEnabled()) return NotFound();
        if (page is < 1 or > 100) return BadRequest();
        var result = await _client.GetSharedRequestsAsync(page, cancellationToken).ConfigureAwait(false);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(DiscoveryDtos.SharedRequests(result));
    }

    private bool TryUser(out Guid id)
    {
        id = Guid.Empty;
        return User.Identity?.IsAuthenticated == true && RecentlyAddedPolicy.TryGetUserId(User, out id) && _userExists(id);
    }

    [HttpGet("RequestOptions")]
    public async Task<ActionResult<RequestOptions>> GetRequestOptions([FromQuery] string? mediaType, [FromQuery] int mediaId, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var id)) return Forbid();
        if (mediaType is not ("movie" or "tv") || mediaId < 1 || mediaId > 100_000_000) return BadRequest();
        var result = await _client.GetRequestOptionsAsync(id, mediaType, mediaId, cancellationToken).ConfigureAwait(false);
        return result.Failure is null ? Ok(result.Value) : StatusCode(result.Failure == SeerrFailure.UserNotMapped ? 403 : 502);
    }

    [HttpPost("Requests")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<CreatedSeerrRequest>> CreateRequest([FromBody] JsonElement body, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var id)) return Forbid();
        if (!TrySelection(body, out var selection)) return BadRequest("Invalid request selection.");
        var result = await _client.CreateRequestAsync(id, selection!, cancellationToken).ConfigureAwait(false);
        return result.Failure switch
        {
            null => Created((string?)null, result.Value),
            SeerrFailure.PermissionDenied or SeerrFailure.UserNotMapped => StatusCode(StatusCodes.Status403Forbidden),
            SeerrFailure.AlreadyRequested => StatusCode(StatusCodes.Status409Conflict),
            SeerrFailure.InvalidSeason => BadRequest("Selected season does not exist for this show."),
            SeerrFailure.CsrfUnsupported => StatusCode(StatusCodes.Status502BadGateway, "Seerr CSRF protection is enabled; API-key request creation is unsupported."),
            _ => StatusCode(StatusCodes.Status502BadGateway)
        };
    }

    private static bool TrySelection(JsonElement body, out CreateSeerrRequest? selection)
    {
        selection = null;
        if (body.ValueKind != JsonValueKind.Object) return false;
        string? type = null;
        int mediaId = 0;
        int[]? seasons = null;
        bool is4k = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return false;
            switch (property.Name)
            {
                case "mediaType":
                    if (property.Value.ValueKind != JsonValueKind.String) return false;
                    type = property.Value.GetString();
                    break;
                case "mediaId":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out mediaId)) return false;
                    break;
                case "is4k":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                    is4k = property.Value.GetBoolean();
                    break;
                case "seasons":
                    if (property.Value.ValueKind != JsonValueKind.Array) return false;
                    var chosen = new List<int>();
                    var distinct = new HashSet<int>();
                    foreach (var season in property.Value.EnumerateArray())
                    {
                        if (chosen.Count >= 100 || season.ValueKind != JsonValueKind.Number || !season.TryGetInt32(out var number) || number < 1 || number > 1000 || !distinct.Add(number)) return false;
                        chosen.Add(number);
                    }
                    seasons = chosen.ToArray();
                    break;
                default: return false;
            }
        }
        if (mediaId < 1 || mediaId > 100_000_000 || type is not ("movie" or "tv") ||
            (type == "movie" && seasons is not null) || (type == "tv" && seasons is not { Length: > 0 })) return false;
        selection = new(type, mediaId, seasons, is4k);
        return true;
    }

    [HttpGet("Discovery")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DiscoveryResponse>> GetDiscovery([FromQuery] int page = 1, CancellationToken cancellationToken = default,
        [FromQuery] int? moviePage = null, [FromQuery] int? tvPage = null, [FromQuery] int? requestsPage = null)
    {
        if (!TryUser(out var id)) return Forbid();
        var bundle = await _client.GetDiscoveryAsync(id, moviePage ?? page, tvPage ?? page, requestsPage ?? page, 20, cancellationToken).ConfigureAwait(false);
        return Ok(new DiscoveryResponse(DiscoveryDtos.Catalog(bundle.Movies, "movie"), DiscoveryDtos.Catalog(bundle.Tv, "tv"), DiscoveryDtos.Requests(bundle.Requests)));
    }

    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SourceResult<CatalogItem>>> GetSearch([FromQuery] string? query, [FromQuery] int page = 1, CancellationToken cancellationToken = default)
    {
        if (!TryUser(out var id)) return Forbid();
        var trimmed = query?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 200) return BadRequest("Invalid query length.");
        var path = $"api/v1/search?query={Uri.EscapeDataString(trimmed)}&page={Math.Clamp(page, 1, 100)}";
        var result = await _client.GetUserReadAsync(id, path, cancellationToken).ConfigureAwait(false);
        return Ok(DiscoveryDtos.Catalog(result));
    }
}
