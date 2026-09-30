using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Rowan.Jellyfin.Plugin.Home;

namespace Rowan.Jellyfin.Plugin.Discovery;

public sealed record TitleDetails(
    [property: JsonPropertyName("Title")] string Title,
    [property: JsonPropertyName("Overview")] string? Overview,
    [property: JsonPropertyName("PosterPath")] string? PosterPath,
    [property: JsonPropertyName("MediaType")] string MediaType,
    [property: JsonPropertyName("TmdbId")] int TmdbId,
    [property: JsonPropertyName("MediaStatus")] int? MediaStatus,
    [property: JsonPropertyName("LibraryItemId")] Guid? LibraryItemId,
    [property: JsonPropertyName("CanRequest")] bool CanRequest,
    [property: JsonPropertyName("CanRequest4k")] bool CanRequest4k,
    [property: JsonPropertyName("Seasons")] int[] Seasons,
    [property: JsonPropertyName("Date")] string? Date = null,
    [property: JsonPropertyName("LibraryStatus")] string LibraryStatus = "unknown");
public sealed record TitleDetailResult(TitleDetails? Value, Guid? JellyfinHint, SeerrFailure? Failure);
public sealed record TitleLibraryCandidate(Guid Id, string Type, string? TmdbId, bool Visible, Guid RootId = default);

public sealed record TitleLibraryResolution(Guid? ItemId, string Status);

/// <summary>Provider identity and current access, never titles or Seerr status, establish membership.</summary>
public static class TitleLibraryPolicy
{
    public static Guid? Resolve(IEnumerable<TitleLibraryCandidate> candidates, string type, int tmdbId, Guid? hint,
        Func<TitleLibraryCandidate, bool>? revalidate = null)
    {
        var matches = candidates.Where(item => item.Visible && item.Id != Guid.Empty && item.Type == type &&
            item.TmdbId == tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .DistinctBy(item => item.Id).OrderBy(item => item.Id).ToArray();
        // An accessible hint is a preference only. A stale/foreign hint cannot veto a verified match.
        return matches.OrderByDescending(item => item.Id == hint)
            .FirstOrDefault(item => revalidate is null || revalidate(item))?.Id;
    }

    public const int CandidateLimit = 32;

    public static InternalItemsQuery CreateQuery(User user, Guid[] roots, string type, int tmdb) => new(user)
    {
        Recursive = true, AncestorIds = roots,
        IncludeItemTypes = type == "movie" ? [BaseItemKind.Movie] : [BaseItemKind.Series],
        HasAnyProviderId = new Dictionary<string, string> { ["Tmdb"] = tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        DtoOptions = new DtoOptions { Fields = [ItemFields.ProviderIds], EnableImages = false, EnableUserData = false },
        IsVirtualItem = false, Limit = CandidateLimit + 1, EnableTotalRecordCount = false,
        GroupByPresentationUniqueKey = false, IncludeAlternateVersions = true
    };

    public static TitleLibraryResolution Lookup(User user, Guid[] roots, string type, int tmdb, Guid? hint,
        Func<InternalItemsQuery, IEnumerable<TitleLibraryCandidate>> query, Func<TitleLibraryCandidate, bool> revalidate)
    {
        // Never allow an empty ancestor filter to accidentally become an unrestricted query.
        if (roots.Length == 0) return new(null, "absent");
        var candidates = query(CreateQuery(user, roots, type, tmdb)).Take(CandidateLimit + 1).ToArray();
        // Bounded duplicate budget; truncation is uncertainty, not proof of absence or uniqueness.
        if (candidates.Length > CandidateLimit) return new(null, "unknown");
        var match = Resolve(candidates, type, tmdb, hint, revalidate);
        if (match is not null) return new(match, "present");
        return new(null, candidates.Length == 0 ? "absent" : "unknown");
    }
}

[ApiController]
[Route("3picFin")]
[Authorize]
public sealed class TitleDetailsController : ControllerBase
{
    private readonly SeerrClient _seerr;
    private readonly Func<Guid, bool> _userExists;
    private readonly Func<Guid, string, int, Guid?, TitleLibraryResolution> _resolve;
    private readonly Func<Guid, int, TitleLibraryResolution>? _resolve4k;

    [ActivatorUtilitiesConstructor]
    public TitleDetailsController(SeerrClient seerr, IUserManager users, ILibraryManager libraries, IMediaSourceManager mediaSources)
        : this(seerr, id => users.GetUserById(id) is not null,
            (id, type, tmdb, hint) => ResolveLibrary(users, libraries, id, type, tmdb, hint),
            (id, tmdb) => ResolveLibrary(users, libraries, id, "movie", tmdb, null, item => Is4kMovie(mediaSources, item))) { }

    public TitleDetailsController(SeerrClient seerr, Func<Guid, bool> userExists, Func<Guid, string, int, Guid?, TitleLibraryResolution> resolve, Func<Guid, int, TitleLibraryResolution>? resolve4k = null)
    {
        _seerr = seerr; _userExists = userExists; _resolve = resolve; _resolve4k = resolve4k;
    }

    [HttpGet("TitleDetails")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TitleDetails>> GetTitleDetails([FromQuery] string? mediaType, [FromQuery] int mediaId, CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true || !RecentlyAddedPolicy.TryGetUserId(User, out var id) || !_userExists(id)) return Forbid();
        if (mediaType is not ("movie" or "tv") || mediaId is < 1 or > 100_000_000) return BadRequest();
        Response.Headers.CacheControl = "private, no-store";
        var result = await _seerr.GetTitleDetailAsync(id, mediaType, mediaId, cancellationToken).ConfigureAwait(false);
        if (result.Failure is not null || result.Value is null)
            return StatusCode(result.Failure == SeerrFailure.UserNotMapped ? 403 : 502);
        TitleLibraryResolution membership;
        try { membership = _resolve(id, mediaType, mediaId, result.JellyfinHint); }
        catch (Exception) { membership = new(null, "unavailable"); }
        var can4k = result.Value.CanRequest4k;
        if (can4k && mediaType == "movie" && _resolve4k is not null)
        {
            try { can4k = _resolve4k(id, mediaId).Status == "absent"; }
            catch (Exception) { can4k = false; }
        }
        return Ok(result.Value with { CanRequest4k = can4k, LibraryItemId = membership.ItemId, LibraryStatus = membership.Status,
            CanRequest = result.Value.CanRequest && (mediaType != "movie" || membership.Status == "absent") });
    }

    internal static bool Is4kMovie(IMediaSourceManager sources, BaseItem item)
    {
        var streams = sources.GetMediaStreams(item.Id).Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video).ToArray();
        if (streams.Length == 0 || streams.Any(s => !s.Width.HasValue))
            throw new InvalidOperationException("Local video variant unknown.");
        // Match Seerr v3.4.1 Jellyfin scanner classification, including cropped video.
        return streams.Any(s => s.Width > 2000);
    }

    internal static TitleLibraryResolution ResolveLibrary(IUserManager users, ILibraryManager libraries, Guid userId, string type, int tmdb, Guid? hint, Func<BaseItem, bool>? variant = null)
    {
        var user = users.GetUserById(userId);
        if (user is null) return new(null, "unknown");
        Guid[] Roots(User current) => libraries.GetUserRootFolder().GetChildren(current, true).OfType<CollectionFolder>()
            .Where(folder => folder.IsVisibleStandalone(current)).SelectMany(folder => folder.PhysicalFolderIds)
            .Where(id => id != Guid.Empty).Distinct().ToArray();
        var roots = Roots(user);
        return TitleLibraryPolicy.Lookup(user, roots, type, tmdb, hint, query =>
        {
            var items = libraries.GetItemsResult(query).Items;
            if (items.Count > TitleLibraryPolicy.CandidateLimit) throw new InvalidOperationException("Library candidates incomplete.");
            return items.Where(item => variant is null || variant(item)).Select(item =>
            {
                var root = item.GetAncestorIds().FirstOrDefault(roots.Contains);
                return new TitleLibraryCandidate(item.Id, item is MediaBrowser.Controller.Entities.Movies.Movie ? "movie" :
                    item is MediaBrowser.Controller.Entities.TV.Series ? "tv" : "other",
                    item.ProviderIds.TryGetValue("Tmdb", out var providerId) ? providerId : null,
                    root != Guid.Empty && !item.IsVirtualItem && item.IsVisibleStandalone(user), root);
            });
        }, candidate =>
        {
            // Re-read user, roots, item identity and every ancestor after the targeted query.
            // The host calls this endpoint again immediately before navigation.
            var currentUser = users.GetUserById(userId);
            if (currentUser is null || !Roots(currentUser).Contains(candidate.RootId)) return false;
            var currentRoot = libraries.GetItemById(candidate.RootId);
            var currentItem = libraries.GetItemById(candidate.Id);
            if (currentRoot is not Folder || !currentRoot.IsVisibleStandalone(currentUser) ||
                currentItem is null || (variant is not null && !variant(currentItem)) || currentItem.IsVirtualItem || !currentItem.IsVisibleStandalone(currentUser) ||
                (type == "movie" ? currentItem is not MediaBrowser.Controller.Entities.Movies.Movie :
                    currentItem is not MediaBrowser.Controller.Entities.TV.Series) ||
                !currentItem.ProviderIds.TryGetValue("Tmdb", out var provider) ||
                provider != tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture)) return false;
            var ancestors = currentItem.GetAncestorIds().ToArray();
            if (!ancestors.Contains(candidate.RootId)) return false;
            foreach (var parentId in ancestors.TakeWhile(parent => parent != candidate.RootId))
            {
                var parent = libraries.GetItemById(parentId);
                if (parent is null || !parent.IsVisibleStandalone(currentUser)) return false;
            }
            return true;
        });
    }
}
