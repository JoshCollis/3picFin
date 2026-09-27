using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
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
    [property: JsonPropertyName("Date")] string? Date = null);
public sealed record TitleDetailResult(TitleDetails? Value, Guid? JellyfinHint, SeerrFailure? Failure);
public sealed record TitleLibraryCandidate(Guid Id, string Type, string? TmdbId, bool Visible, Guid RootId = default);

/// <summary>Only a unique eligible TMDb match can become a playable item ID.</summary>
public static class TitleLibraryPolicy
{
    public static Guid? Resolve(IEnumerable<TitleLibraryCandidate> candidates, string type, int tmdbId, Guid? hint,
        Func<TitleLibraryCandidate, bool>? revalidate = null)
    {
        var matches = candidates.Where(item => item.Visible && item.Id != Guid.Empty && item.Type == type &&
            item.TmdbId == tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Take(2).ToArray();
        return matches.Length == 1 && (hint is null || hint == matches[0].Id) &&
            (revalidate is null || revalidate(matches[0])) ? matches[0].Id : null;
    }
}

[ApiController]
[Route("3picFin")]
[Authorize]
public sealed class TitleDetailsController : ControllerBase
{
    private readonly SeerrClient _seerr;
    private readonly Func<Guid, bool> _userExists;
    private readonly Func<Guid, string, int, Guid?, Guid?> _resolve;

    [ActivatorUtilitiesConstructor]
    public TitleDetailsController(SeerrClient seerr, IUserManager users, ILibraryManager libraries)
        : this(seerr, id => users.GetUserById(id) is not null,
            (id, type, tmdb, hint) => ResolveLibrary(users, libraries, id, type, tmdb, hint)) { }

    public TitleDetailsController(SeerrClient seerr, Func<Guid, bool> userExists, Func<Guid, string, int, Guid?, Guid?> resolve)
    {
        _seerr = seerr; _userExists = userExists; _resolve = resolve;
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
        var itemId = _resolve(id, mediaType, mediaId, result.JellyfinHint);
        return Ok(result.Value with { LibraryItemId = itemId });
    }

    private static Guid? ResolveLibrary(IUserManager users, ILibraryManager libraries, Guid userId, string type, int tmdb, Guid? hint)
    {
        var user = users.GetUserById(userId);
        if (user is null) return null;
        var roots = libraries.GetUserRootFolder().GetChildren(user, true).OfType<CollectionFolder>()
            .Select(folder => folder.Id).Distinct().Take(9).ToArray();
        if (roots.Length == 0 || roots.Length > 8) return null;
        var candidates = new List<TitleLibraryCandidate>();
        foreach (var root in roots)
        {
            // Finite scan: never claim uniqueness if the result set exceeds the inspection budget.
            var items = libraries.GetItemsResult(new InternalItemsQuery(user)
            {
                ParentId = root, Recursive = true, IncludeItemTypes = type == "movie" ? [BaseItemKind.Movie] : [BaseItemKind.Series],
                IsVirtualItem = false, Limit = 257, EnableTotalRecordCount = false
            }).Items;
            if (items.Count > 256) return null;
            foreach (var item in items)
            {
                var ancestors = item.GetAncestorIds().ToArray();
                var eligible = !item.IsVirtualItem && item.IsVisibleStandalone(user) && ancestors.Contains(root);
                foreach (var parentId in ancestors.TakeWhile(parent => parent != root))
                {
                    var parent = libraries.GetItemById(parentId);
                    if (parent is null || !parent.IsVisibleStandalone(user)) { eligible = false; break; }
                }
                candidates.Add(new(item.Id, item is MediaBrowser.Controller.Entities.Movies.Movie ? "movie" :
                    item is MediaBrowser.Controller.Entities.TV.Series ? "tv" : "other", item.ProviderIds.TryGetValue("Tmdb", out var providerId) ? providerId : null, eligible, root));
            }
        }
        return TitleLibraryPolicy.Resolve(candidates, type, tmdb, hint, candidate =>
        {
            // Re-read all visibility gates after the scan: a queried snapshot can predate revocation.
            var currentUser = users.GetUserById(userId);
            if (currentUser is null) return false;
            var currentRoots = libraries.GetUserRootFolder().GetChildren(currentUser, true).OfType<CollectionFolder>()
                .Select(folder => folder.Id).Distinct().Take(9).ToArray();
            if (currentRoots.Length > 8 || !currentRoots.Contains(candidate.RootId)) return false;
            var currentRoot = libraries.GetItemById(candidate.RootId);
            var currentItem = libraries.GetItemById(candidate.Id);
            if (currentRoot is not CollectionFolder || !currentRoot.IsVisibleStandalone(currentUser) ||
                currentItem is null || currentItem.IsVirtualItem || !currentItem.IsVisibleStandalone(currentUser) ||
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
