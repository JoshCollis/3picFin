using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Fail-closed row names and a second visibility boundary after Jellyfin's user queries.</summary>
public static class NativeRowsPolicy
{
    /// <summary>Rows available in this first slice.</summary>
    public static readonly string[] Kinds = ["ContinueWatching", "NextUp", "LatestMovies", "LatestShows", "MyMedia", "ContinueWatchingNextUp", "Collections"];

    /// <summary>Reject arbitrary row dispatch.</summary>
    public static bool IsSupported(string kind) => Kinds.Contains(kind, StringComparer.Ordinal);

    /// <summary>Retain only descendants of currently visible libraries, with an explicit cap.</summary>
    public static BaseItem[] WithinVisibleLibraries(IEnumerable<BaseItem> items, IReadOnlyCollection<Guid> visibleIds, int limit,
        Func<BaseItem, bool>? isVisible = null)
    {
        if (visibleIds.Count == 0 || limit <= 0) return [];
        var ids = visibleIds.ToHashSet();
        return items.Where(item => item.GetAncestorIds().Any(ids.Contains) && (isVisible?.Invoke(item) ?? true))
            .DistinctBy(item => item.Id).Take(limit).ToArray();
    }

    // Jellyfin's IsUnaired treats the entire local premiere day as unaired. A precise,
    // elapsed timestamp can pass; a midnight/date-only or timezone-unknown value cannot.
    private static bool IsAired(Episode episode, DateTime nowUtc)
    {
        if (!episode.PremiereDate.HasValue) return true;
        var premiere = episode.PremiereDate.Value;
        var today = nowUtc.ToLocalTime().Date;
        if (premiere.Kind == DateTimeKind.Unspecified)
            return premiere.Date < today; // No trustworthy offset for a same-day air time.
        if (premiere.TimeOfDay == TimeSpan.Zero &&
            (premiere.ToLocalTime().Date >= today ||
             (premiere.Kind == DateTimeKind.Utc && premiere.Date >= nowUtc.Date))) return false;
        if (premiere.ToLocalTime().Date > today) return false;
        return premiere.ToUniversalTime() <= nowUtc;
    }

    /// <summary>Refill a user query after entitlement checks, with at most eight bounded calls.</summary>
    public static BaseItem[] CollectPaged(int pageSize, int limit, IReadOnlyCollection<Guid> libraryIds,
        Func<int, int, BaseItem[]> fetch, Func<BaseItem, bool> isVisible, bool nextUp, DateTime? nowUtc = null)
    {
        if (pageSize <= 0 || limit <= 0 || libraryIds.Count == 0) return [];
        var now = nowUtc ?? DateTime.UtcNow;
        var found = new List<BaseItem>();
        var seen = new HashSet<Guid>();
        for (var page = 0; page < 8 && found.Count < limit; page++)
        {
            var batch = fetch(page * pageSize, pageSize);
            foreach (var item in batch)
            {
                if (item.Id == Guid.Empty || !seen.Add(item.Id) ||
                    !item.GetAncestorIds().Any(libraryIds.Contains) || !isVisible(item) ||
                    (nextUp && (item is not Episode episode || !IsAired(episode, now) || episode.IsVirtualItem))) continue;
                found.Add(item);
                if (found.Count == limit) break;
            }
            if (batch.Length < pageSize) break;
        }
        return found.ToArray();
    }

    /// <summary>HSS combined-row ordering: resume first, deduplicate Next Up, optionally hide watched, then playback date.</summary>
    public static BaseItemDto[] MergePlayback(IEnumerable<BaseItemDto> resume, IEnumerable<BaseItemDto> nextUp,
        bool hideWatched, int limit, Func<Guid, DateTime?> seriesLastPlayed)
    {
        if (limit <= 0) return [];
        // Inputs are bounded by their source queries; resolve each series at most once.
        var candidates = resume.Concat(nextUp).Where(item => item.Id != Guid.Empty)
            .DistinctBy(item => item.Id).Where(item => !hideWatched || item.UserData?.Played != true).ToArray();
        var seriesDates = candidates.Where(item => item.SeriesId.HasValue && item.UserData?.LastPlayedDate is null)
            .Select(item => item.SeriesId!.Value).Distinct().ToDictionary(id => id, seriesLastPlayed);
        return candidates.OrderByDescending(item => item.UserData?.LastPlayedDate ??
            (item.SeriesId.HasValue && seriesDates.TryGetValue(item.SeriesId.Value, out var played) ? played : null) ??
            item.DateCreated ?? DateTime.MinValue).Take(limit).ToArray();
    }

    /// <summary>Collection cards must have a visible collection root and at least one independently entitled member.</summary>
    public static BoxSet[] CollectCollections(int pageSize, int limit, IReadOnlyCollection<Guid> collectionFolders,
        IReadOnlyCollection<Guid> mediaLibraries, Func<Guid, int, int, BoxSet[]> fetch,
        Func<BoxSet, IEnumerable<BaseItem>> members, Func<BaseItem, bool> isVisible)
    {
        if (pageSize <= 0 || limit <= 0 || collectionFolders.Count == 0 || mediaLibraries.Count == 0) return [];
        var result = new List<BoxSet>();
        var seen = new HashSet<Guid>();
        foreach (var folder in collectionFolders.Take(8))
        {
            // Each source is ordered newest-first. Keep up to a row's worth per folder,
            // then rank across folders; filling the first folder cannot end the scan.
            var localCount = 0;
            for (var page = 0; page < 8 && localCount < limit; page++)
            {
                var batch = fetch(folder, page * pageSize, pageSize);
                foreach (var box in batch)
                {
                    if (box.Id == Guid.Empty || !seen.Add(box.Id) || !box.GetAncestorIds().Contains(folder) ||
                        !isVisible(box) || !members(box).Take(32).Any(item =>
                            item.GetAncestorIds().Any(mediaLibraries.Contains) && isVisible(item))) continue;
                    result.Add(box);
                    if (++localCount == limit) break;
                }
                if (batch.Length < pageSize) break;
            }
        }
        return result.OrderByDescending(box => box.DateLastMediaAdded).Take(limit).ToArray();
    }

    /// <summary>Fetch bounded pages until enough distinct, authorized candidates survive.</summary>
    public static BaseItem[] CollectLatest(int pageSize, int limit, bool shows, IReadOnlyCollection<Guid> libraryIds,
        Func<Guid, int, int, BaseItem[]> fetch, Func<BaseItem, bool> isVisible,
        Func<Episode, BaseItem?>? resolveSeries = null, DateTime? nowUtc = null)
    {
        if (pageSize <= 0 || limit <= 0 || libraryIds.Count == 0) return [];
        var now = nowUtc ?? DateTime.UtcNow;
        var found = new Dictionary<Guid, (BaseItem Item, DateTime? Premiere)>();
        foreach (var library in libraryIds.Take(64))
        {
            var localUnique = new HashSet<Guid>();
            for (var page = 0; page < 8 && localUnique.Count < limit; page++)
            {
                var batch = fetch(library, page * pageSize, pageSize);
                foreach (var item in batch)
                {
                    if (!item.GetAncestorIds().Contains(library) || !isVisible(item) ||
                        (shows && (item is not Episode episode || !IsAired(episode, now) || episode.IsVirtualItem))) continue;
                    var key = shows ? ((Episode)item).SeriesId : item.Id;
                    if (key == Guid.Empty || (found.TryGetValue(key, out var previous) &&
                        (previous.Premiere == item.PremiereDate || previous.Premiere > item.PremiereDate))) continue;
                    var resolved = shows && resolveSeries is not null ? resolveSeries((Episode)item) : item;
                    if (resolved is null || (shows && resolveSeries is not null &&
                        (resolved is not Series || resolved.Id != key ||
                         !resolved.GetAncestorIds().Any(libraryIds.Contains) || !isVisible(resolved)))) continue;
                    found[key] = (resolved, item.PremiereDate);
                    localUnique.Add(key);
                    if (localUnique.Count == limit) break;
                }
                if (batch.Length < pageSize) break;
            }
        }
        return found.Values.OrderByDescending(entry => entry.Premiere).Take(limit).Select(entry => entry.Item).ToArray();
    }

    /// <summary>A grouping grant derives from a currently visible library and the user's grouping setting.</summary>
    public sealed record GroupedLibrary(Guid Id, CollectionType? Type);

    /// <summary>Preserve user-specific and grouped views without trusting their synthetic IDs as library IDs.</summary>
    public static BaseItem[] VisibleViews(IEnumerable<Folder> views, IReadOnlyCollection<Guid> visibleIds,
        IReadOnlyCollection<GroupedLibrary> grouped)
    {
        var ids = visibleIds.ToHashSet();
        return views.Where(view => view is UserView userView
                ? userView.DisplayParentId != Guid.Empty
                    ? ids.Contains(userView.DisplayParentId)
                    : grouped.Any(folder => ids.Contains(folder.Id) &&
                        (folder.Type == userView.ViewType || folder.Type is null) &&
                        (userView.ViewType is CollectionType.movies or CollectionType.tvshows))
                : ids.Contains(view.Id))
            .DistinctBy(view => view.Id).Take(64).Cast<BaseItem>().ToArray();
    }
}
