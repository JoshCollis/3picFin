using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Rowan.Jellyfin.Plugin.Assistant;

public sealed record AiViewingContext(AiCard[] Recent, AiCard[] Favorites, AiProgress[] InProgress);
public sealed record AiProgress(AiCard Title, long? MinutesWatched);

public sealed class AiLibrary(IUserManager users, ILibraryManager libraries, IUserDataManager userData)
{
    public static bool Allowed(User? user) => user is not null && !user.HasPermission(PermissionKind.IsDisabled) &&
        user.MaxParentalRatingScore is null && user.MaxParentalRatingSubScore is null &&
        !user.GetPreferenceValues<string>(PreferenceKind.BlockedTags).Any() &&
        !user.GetPreferenceValues<string>(PreferenceKind.AllowedTags).Any() &&
        !user.GetPreferenceValues<string>(PreferenceKind.BlockUnratedItems).Any();

    private User User(Guid id) => users.GetUserById(id) is { } user && Allowed(user) ? user : throw new AiException("The assistant is unavailable for this account.", 403);
    private Guid[] Roots(User user) => libraries.GetUserRootFolder().GetChildren(user, true).OfType<CollectionFolder>()
        .Where(f => f.IsVisibleStandalone(user)).SelectMany(f => f.PhysicalFolderIds).Where(id => id != Guid.Empty).Distinct().ToArray();

    private bool Visible(User user, BaseItem item, Guid[] roots)
    {
        if (item.IsVirtualItem || !item.IsVisibleStandalone(user)) return false;
        var ancestors = item.GetAncestorIds().ToArray();
        if (!ancestors.Any(roots.Contains)) return false;
        foreach (var parentId in ancestors)
        {
            if (libraries.GetItemById(parentId) is not { } parent || !parent.IsVisibleStandalone(user)) return false;
            if (roots.Contains(parentId)) return true;
        }
        return false;
    }

    public AiCard? Get(Guid userId, Guid itemId)
    {
        var user = User(userId);
        var item = libraries.GetItemById(itemId);
        return item is not null && item is MediaBrowser.Controller.Entities.Movies.Movie or MediaBrowser.Controller.Entities.TV.Series &&
            Visible(user, item, Roots(user)) ? Card(item) : null;
    }

    public static InternalItemsQuery EpisodeQuery(User user, Guid series, int? season, int page) => new(user)
    {
        AncestorIds = [series], Recursive = true, IncludeItemTypes = [BaseItemKind.Episode], IsVirtualItem = false,
        ParentIndexNumber = season, StartIndex = (page - 1) * 20, Limit = 21, EnableTotalRecordCount = false,
        OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)],
        DtoOptions = new DtoOptions { Fields = [], EnableUserData = false, EnableImages = false }
    };

    public object Episodes(Guid userId, Guid series, int? season, int page)
    {
        var user = User(userId);
        if (Get(userId, series) is not { MediaType: "tv" } title) return new { error = "Series unavailable or not visible." };
        var rows = libraries.GetItemsResult(EpisodeQuery(user, series, season, page)).Items;
        user = User(userId); var roots = Roots(user);
        if (Get(userId, series) is null) return new { error = "Series is no longer visible." };
        var episodes = rows.Take(20).OfType<MediaBrowser.Controller.Entities.TV.Episode>().Where(e => e.SeriesId == series && Visible(user, e, roots))
            .Select(e => new { title = AiJson.Clip(e.Name, 200), season = e.ParentIndexNumber, episode = e.IndexNumber, endEpisode = e.IndexNumberEnd,
                runtimeMinutes = e.RunTimeTicks / TimeSpan.TicksPerMinute }).ToArray();
        return new { series = title, episodes, page, nextPage = rows.Count > 20 ? page + 1 : (int?)null, complete = rows.Count <= 20,
            note = "Visible, non-virtual library episodes only. Continue pages before claiming a whole season is present; catalog/request completion is not local availability." };
    }

    public static InternalItemsQuery SearchQuery(User user, Guid[] roots, string query, string genre, string type, bool? watched, int page, int? maxMinutes = null,
        string sort = "name", string person = "", int? year = null, bool? favorite = null) => new(user)
    {
        Recursive = true, AncestorIds = roots, SearchTerm = string.IsNullOrWhiteSpace(query) ? null : query,
        Genres = string.IsNullOrWhiteSpace(genre) ? [] : [genre], IsPlayed = watched,
        Person = string.IsNullOrWhiteSpace(person) ? null : person, Years = year.HasValue ? [year.Value] : [], IsFavorite = favorite,
        IncludeItemTypes = type == "movie" ? [BaseItemKind.Movie] : type == "tv" ? [BaseItemKind.Series] : [BaseItemKind.Movie, BaseItemKind.Series],
        IsVirtualItem = false, StartIndex = (page - 1) * 20, Limit = 20, EnableTotalRecordCount = false,
        OrderBy = sort == "recent" ? [(ItemSortBy.DateCreated, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending)] :
            sort == "rating" ? [(ItemSortBy.CommunityRating, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending)] : [(ItemSortBy.SortName, SortOrder.Ascending)],
        DtoOptions = new DtoOptions { Fields = [ItemFields.ProviderIds], EnableUserData = false, EnableImages = false }
    };

    public object Search(Guid userId, string query, string genre, string type, bool? watched, int page, int? maxMinutes = null,
        string sort = "name", string person = "", int? year = null, bool? favorite = null)
    {
        var user = User(userId);
        var roots = Roots(user);
        if (roots.Length == 0) return new { items = Array.Empty<AiCard>(), nextPage = (int?)null };
        var queryObject = SearchQuery(user, roots, query, genre, type, watched, page, maxMinutes, sort, person, year, favorite);
        var rows = libraries.GetItemsResult(queryObject).Items;
        // Fresh roots/user and ancestor checks after the query. No first-N-library allowlist.
        user = User(userId); roots = Roots(user);
        var items = rows.Where(i => Visible(user, i, roots) && (maxMinutes is null || i.RunTimeTicks is > 0 && i.RunTimeTicks <= maxMinutes * TimeSpan.TicksPerMinute))
            .Select(i => Card(i) with { Watched = userData.GetUserData(user, i)?.Played == true }).ToArray();
        return new { items, page, nextPage = rows.Count == 20 ? page + 1 : (int?)null, complete = rows.Count < 20,
            note = maxMinutes.HasValue ? "Runtime is applied after each database page; an empty page may have more matches on later pages. Unknown runtimes are excluded. Series runtimes, when present, are episode estimates." : null };
    }

    public AiViewingContext Personalize(Guid userId)
    {
        var user = User(userId); var roots = Roots(user);
        if (roots.Length == 0) return new([], [], []);
        AiCard[] Read(bool favorites)
        {
            var rows = libraries.GetItemsResult(new InternalItemsQuery(user)
            {
                AncestorIds = roots, Recursive = true, IsVirtualItem = false, Limit = 15, EnableTotalRecordCount = false,
                IncludeItemTypes = favorites ? [BaseItemKind.Movie, BaseItemKind.Series] : [BaseItemKind.Movie, BaseItemKind.Episode],
                IsFavorite = favorites ? true : null, IsPlayed = favorites ? null : true,
                OrderBy = favorites ? [(ItemSortBy.SortName, SortOrder.Ascending)] : [(ItemSortBy.DatePlayed, SortOrder.Descending)]
            }).Items;
            var current = User(userId); var currentRoots = Roots(current);
            return rows.Where(i => Visible(current, i, currentRoots)).Select(i =>
                i is MediaBrowser.Controller.Entities.TV.Episode episode ? Get(userId, episode.SeriesId) : Card(i))
                .Where(i => i is not null).Select(i => i! with { Watched = favorites || i!.MediaType == "tv" ? null : true }).DistinctBy(i => i.Key).ToArray();
        }
        var progress = libraries.GetItemsResult(new InternalItemsQuery(user)
        {
            AncestorIds = roots, Recursive = true, IsVirtualItem = false, IsResumable = true, Limit = 10, EnableTotalRecordCount = false,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode], OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)]
        }).Items;
        user = User(userId); roots = Roots(user);
        var inProgress = progress.Where(i => Visible(user, i, roots)).Select(i => new
        {
            title = i is MediaBrowser.Controller.Entities.TV.Episode episode ? Get(userId, episode.SeriesId) : Card(i),
            minutes = userData.GetUserData(user, i)?.PlaybackPositionTicks / TimeSpan.TicksPerMinute
        }).Where(i => i.title is not null).Select(i => new AiProgress(i.title!, i.minutes)).DistinctBy(i => i.Title.Key).ToArray();
        return new(Read(false), Read(true), inProgress);
    }

    public static AiCard Card(BaseItem item)
    {
        int? tmdb = item.ProviderIds.TryGetValue("Tmdb", out var value) && int.TryParse(value, out var id) && id > 0 ? id : null;
        var type = item is MediaBrowser.Controller.Entities.TV.Series ? "tv" : "movie";
        return new("library:" + item.Id.ToString("N"), AiJson.Clip(item.Name, 200), type, tmdb, item.Id,
            item.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture), Overview: AiJson.Clip(item.Overview, 350),
            RuntimeMinutes: item.RunTimeTicks is > 0 ? (int)Math.Min(10000, item.RunTimeTicks.Value / TimeSpan.TicksPerMinute) : null,
            Genres: item.Genres.Take(8).Select(g => AiJson.Clip(g, 80)).ToArray(), CommunityRating: item.CommunityRating);
    }
}
