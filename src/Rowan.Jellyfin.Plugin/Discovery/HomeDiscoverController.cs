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
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Home;

namespace Rowan.Jellyfin.Plugin.Discovery;

/// <summary>Seerr discovery candidates only; no Jellyfin item identity, availability or playable claim.</summary>
public sealed record HomeDiscoverItem(int TmdbId, string MediaType, string Title, string? PosterPath, string? Date);

[ApiController]
[Route("3picFin/HomeDiscover")]
[Authorize]
public sealed class HomeDiscoverController : ControllerBase
{
    private readonly SeerrClient _client;
    private readonly Func<Guid, bool> _userExists;
    private readonly Func<PluginConfiguration?> _configuration;

    [ActivatorUtilitiesConstructor]
    public HomeDiscoverController(SeerrClient client, IUserManager users) : this(client,
        id => users.GetUserById(id) is not null, () => Plugin.Current?.Configuration) { }

    public HomeDiscoverController(SeerrClient client, Func<Guid, bool> userExists, Func<PluginConfiguration?> configuration)
    {
        _client = client;
        _userExists = userExists;
        _configuration = configuration;
    }

    [HttpGet("Discover")]
    public Task<ActionResult<SourceResult<HomeDiscoverItem>>> GetDiscover([FromQuery] int page = 1, CancellationToken cancellationToken = default) =>
        ReadAsync("trending", null, config => config.DiscoverRowEnabled, page, cancellationToken);

    [HttpGet("DiscoverMovies")]
    public Task<ActionResult<SourceResult<HomeDiscoverItem>>> GetDiscoverMovies([FromQuery] int page = 1, CancellationToken cancellationToken = default) =>
        ReadAsync("movies", "movie", config => config.DiscoverMoviesRowEnabled, page, cancellationToken);

    [HttpGet("DiscoverTV")]
    public Task<ActionResult<SourceResult<HomeDiscoverItem>>> GetDiscoverTv([FromQuery] int page = 1, CancellationToken cancellationToken = default) =>
        ReadAsync("tv", "tv", config => config.DiscoverTvRowEnabled, page, cancellationToken);

    private async Task<ActionResult<SourceResult<HomeDiscoverItem>>> ReadAsync(string source, string? expectedType,
        Func<PluginConfiguration, bool> enabled, int page, CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true || !RecentlyAddedPolicy.TryGetUserId(User, out var userId) || !_userExists(userId)) return Forbid();
        var configuration = _configuration();
        if (configuration?.HomeEnabled != true || !enabled(configuration)) return NotFound();
        if (page is < 1 or > 100) return BadRequest();
        Response.Headers.CacheControl = "private, no-store";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var items = new List<HomeDiscoverItem>(20);
        var seen = new HashSet<(string, int)>();
        var tvChecks = 0;
        int? totalPages = null, totalResults = null;
        for (var next = page; next <= 100 && next < page + 3 && items.Count < 20; next++)
        {
            SeerrResult result;
            try { result = await _client.GetUserReadAsync(userId, $"api/v1/discover/{source}?page={next}", deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Ok(new SourceResult<HomeDiscoverItem>(items, SeerrFailure.UpstreamUnavailable.ToString(), page, totalPages, totalResults));
            }
            if (result.Failure is { } failure) return Ok(new SourceResult<HomeDiscoverItem>(items, failure.ToString(), page, totalPages, totalResults));
            if (result.Value is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return Ok(new SourceResult<HomeDiscoverItem>(items, SeerrFailure.UpstreamUnavailable.ToString(), page, totalPages, totalResults));
            var catalog = DiscoveryDtos.Catalog(result, expectedType);
            totalPages = catalog.TotalPages;
            totalResults = catalog.TotalResults;
            foreach (var candidate in results.EnumerateArray())
            {
                if (items.Count >= 20) break;
                if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    return Ok(new SourceResult<HomeDiscoverItem>(items, SeerrFailure.UpstreamUnavailable.ToString(), page, totalPages, totalResults));
                if (candidate.ValueKind != JsonValueKind.Object) continue;
                var type = candidate.TryGetProperty("mediaType", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : expectedType;
                if (type is not ("movie" or "tv")) continue;
                // Seerr v3.4.1 mapTvResult has no adult field. Explicit adult:true is still vetoed;
                // movies retain the strict adult:false requirement.
                var hasAdult = candidate.TryGetProperty("adult", out var adult);
                if (hasAdult && adult.ValueKind == JsonValueKind.True) continue;
                if (type == "movie" && (!hasAdult || adult.ValueKind != JsonValueKind.False)) continue;
                if (type == "tv" && hasAdult && adult.ValueKind != JsonValueKind.False) continue;
                if (candidate.TryGetProperty("mediaInfo", out var media) && media.ValueKind != JsonValueKind.Null)
                {
                    if (media.ValueKind != JsonValueKind.Object || !media.TryGetProperty("status", out var status) || !status.TryGetInt32(out var state) || state is < 0 or > 100 or 6) continue;
                }
                // Reuse the existing allowlisted catalog conversion, including title, type and poster validation.
                using var one = JsonDocument.Parse("{\"results\":[" + candidate.GetRawText() + "]}");
                var filtered = DiscoveryDtos.Catalog(SeerrResult.Success(one.RootElement), expectedType);
                if (filtered.Items.Count == 0) continue;
                var item = filtered.Items[0];
                if (seen.Contains((item.MediaType, item.TmdbId))) continue;
                if (type == "tv")
                {
                    // At most 20 TV detail reads across all three pages; shared deadline applies to all.
                    // A missing, invalid, or mature US TV rating is not evidence of family-safe content.
                    if (item.TmdbId > 100_000_000 || tvChecks >= 20) continue;
                    tvChecks++;
                    SeerrResult detail;
                    try { detail = await _client.GetUserTvDetailAsync(userId, item.TmdbId, deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return Ok(new SourceResult<HomeDiscoverItem>(items, SeerrFailure.UpstreamUnavailable.ToString(), page, totalPages, totalResults));
                    }
                    if (!HasSafeUsTvRating(detail, item.TmdbId)) continue;
                }
                if (seen.Add((item.MediaType, item.TmdbId))) items.Add(new(item.TmdbId, item.MediaType, item.Title, item.PosterPath, item.Date));
            }
            if (results.GetArrayLength() == 0 || (totalPages is { } last && next >= last)) break;
        }
        return Ok(new SourceResult<HomeDiscoverItem>(items, null, page, totalPages, totalResults));
    }

    private static bool HasSafeUsTvRating(SeerrResult detail, int tmdbId)
    {
        if (detail.Value is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var actualId) || actualId != tmdbId ||
            !root.TryGetProperty("contentRatings", out var ratings) || ratings.ValueKind != JsonValueKind.Object ||
            !ratings.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return false;
        var safe = false;
        foreach (var entry in results.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("iso_3166_1", out var country) ||
                country.ValueKind != JsonValueKind.String || country.GetString() != "US") continue;
            if (!entry.TryGetProperty("rating", out var rating) || rating.ValueKind != JsonValueKind.String ||
                rating.GetString() is not ("TV-Y" or "TV-Y7" or "TV-G" or "TV-PG")) return false;
            safe = true;
        }
        return safe;
    }
}
