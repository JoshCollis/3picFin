using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Bounded native Jellyfin Home queries; never accepts a caller-selected identity or library.</summary>
[ApiController]
[Route("Rowan/Home")]
[Authorize]
public sealed class NativeRowsController : ControllerBase
{
    private static readonly Guid PluginId = Guid.Parse("bd36ab75-0f4a-49b6-92ef-3a93da040c7a");
    private readonly IUserManager _users;
    private readonly ILibraryManager _libraries;
    private readonly IUserViewManager _views;
    private readonly ITVSeriesManager _tv;
    private readonly IDtoService _dtos;
    private readonly IPluginManager _plugins;
    private readonly IUserDataManager _userData;

    /// <summary>Constructs the native row adapter.</summary>
    public NativeRowsController(IUserManager users, ILibraryManager libraries, IUserViewManager views,
        ITVSeriesManager tv, IDtoService dtos, IPluginManager plugins, IUserDataManager userData)
    {
        _users = users;
        _libraries = libraries;
        _views = views;
        _tv = tv;
        _dtos = dtos;
        _plugins = plugins;
        _userData = userData;
    }

    /// <summary>Loads one named row at a time for lazy Home presentation.</summary>
    [HttpGet("Rows/{kind}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<NativeHomeRow> GetRow(string kind)
    {
        if (!RecentlyAddedPolicy.TryGetUserId(User, out var userId)) return Forbid();
        User? user = _users.GetUserById(userId);
        if (user is null) return Forbid();
        if (!NativeRowsPolicy.IsSupported(kind)) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        var config = (_plugins.GetPlugin(PluginId)?.Instance as Plugin)?.Configuration;
        if (config?.HomeEnabled != true ||
            (kind == "ContinueWatchingNextUp" && !config.CombinedPlaybackRowEnabled) ||
            (kind == "Collections" && !config.CollectionsRowEnabled))
            return Ok(new NativeHomeRow(kind, []));

        var visible = _libraries.GetUserRootFolder().GetChildren(user, true)
            .OfType<CollectionFolder>().DistinctBy(folder => folder.Id).Take(64).ToArray();
        var visibleIds = visible.Select(folder => folder.Id).ToArray();
        var options = new DtoOptions();
        if (kind == "ContinueWatchingNextUp")
        {
            var resume = Resume(user, visibleIds.Except(user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes)).ToArray());
            var next = NextUp(user, visibleIds, options);
            var resumeDtos = _dtos.GetBaseItemDtos(resume, options, user);
            var nextDtos = _dtos.GetBaseItemDtos(next, options, user);
            DateTime? LastPlayed(Guid seriesId)
            {
                // HSS checks the two most recent played episodes for each series.
                var series = _libraries.GetItemById<Series>(seriesId, user);
                if (series is null || !series.IsVisibleStandalone(user) ||
                    !series.GetAncestorIds().Any(visibleIds.Contains)) return null;
                return _libraries.GetItemList(new InternalItemsQuery(user)
                {
                    AncestorIds = [seriesId], IncludeItemTypes = [BaseItemKind.Episode], IsPlayed = true,
                    OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)], Limit = 2,
                    EnableTotalRecordCount = false
                }).Where(item => item.GetAncestorIds().Any(visibleIds.Contains) && item.IsVisibleStandalone(user))
                    .Select(item => _userData.GetUserData(user, item)?.LastPlayedDate).FirstOrDefault(date => date.HasValue);
            }
            return Ok(new NativeHomeRow(kind, NativeRowsPolicy.MergePlayback(resumeDtos, nextDtos, config.CombinedPlaybackHideWatched, 36, LastPlayed)));
        }
        if (kind == "Collections")
        {
            var folders = visible.Where(folder => folder.CollectionType == CollectionType.boxsets).Select(folder => folder.Id).ToArray();
            var mediaIds = visibleIds.Except(folders).ToArray();
            var boxsets = NativeRowsPolicy.CollectCollections(32, 16, folders, mediaIds,
                (folder, start, count) => _libraries.GetItemsResult(new InternalItemsQuery(user)
                {
                    ParentId = folder, Recursive = true, IncludeItemTypes = [BaseItemKind.BoxSet],
                    OrderBy = [(ItemSortBy.DateLastContentAdded, SortOrder.Descending)],
                    StartIndex = start, Limit = count, EnableTotalRecordCount = false
                }).Items.OfType<BoxSet>().ToArray(),
                box => box.GetChildren(user, true, new InternalItemsQuery(user) { Limit = 32, EnableTotalRecordCount = false }),
                item => item.IsVisibleStandalone(user));
            return Ok(new NativeHomeRow(kind, _dtos.GetBaseItemDtos(boxsets, options, user)));
        }
        BaseItem[] items;
        switch (kind)
        {
            case "ContinueWatching":
                items = Resume(user, visibleIds.Except(user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes)).ToArray());
                break;
            case "NextUp":
                items = NextUp(user, visibleIds, options);
                break;
            case "LatestMovies":
            case "LatestShows":
                var type = kind == "LatestMovies" ? BaseItemKind.Movie : BaseItemKind.Episode;
                // Query each visible library, rather than HSS's unbounded date-window loop.
                // Skip unrelated libraries; null collection type is a mixed library.
                var matching = visible.Where(folder => folder.CollectionType is null ||
                    folder.CollectionType == (kind == "LatestMovies" ? CollectionType.movies : CollectionType.tvshows));
                var matchingIds = matching.Select(folder => folder.Id).ToArray();
                var latest = NativeRowsPolicy.CollectLatest(32, 16, kind == "LatestShows", matchingIds,
                    (libraryId, start, count) => _libraries.GetItemsResult(new InternalItemsQuery(user)
                    {
                        ParentId = libraryId,
                        Recursive = true,
                        IsVirtualItem = false,
                        MaxPremiereDate = DateTime.UtcNow,
                        IncludeItemTypes = [type],
                        OrderBy = [(ItemSortBy.PremiereDate, SortOrder.Descending)],
                        StartIndex = start,
                        Limit = count,
                        EnableTotalRecordCount = false
                    }).Items.ToArray(), item => item.IsVisibleStandalone(user),
                    kind == "LatestShows" ? episode => _libraries.GetItemById<Series>(episode.SeriesId, user) : null);
                items = latest;
                break;
            default: // MyMedia: Jellyfin's user views preserve hidden-view preferences.
                items = NativeRowsPolicy.VisibleViews(
                    _views.GetUserViews(new UserViewQuery { User = user, IncludeHidden = false }), visibleIds,
                    visible.Where(folder => user.IsFolderGrouped(folder.Id))
                        .Select(folder => new NativeRowsPolicy.GroupedLibrary(folder.Id, folder.CollectionType)).ToArray())
                    .Where(item => item.IsVisibleStandalone(user)).ToArray();
                break;
        }
        // Do not bypass Jellyfin's DTO visibility and parental checks.
        var dtos = _dtos.GetBaseItemDtos(items, options, user);
        return Ok(new NativeHomeRow(kind, dtos));
    }

    private BaseItem[] Resume(User user, Guid[] eligible) => NativeRowsPolicy.CollectPaged(24, 12, eligible,
        (start, count) => _libraries.GetItemsResult(new InternalItemsQuery(user)
        {
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)], IsResumable = true,
            StartIndex = start, Limit = count, Recursive = true, MediaTypes = [MediaType.Video],
            IsVirtualItem = false, CollapseBoxSetItems = false, EnableTotalRecordCount = false,
            AncestorIds = eligible
        }).Items.ToArray(), item => item.IsVisibleStandalone(user), false);

    private BaseItem[] NextUp(User user, Guid[] visibleIds, DtoOptions options) =>
        NativeRowsPolicy.CollectPaged(32, 24, visibleIds, (start, count) =>
            _tv.GetNextUp(new NextUpQuery
            {
                User = user, StartIndex = start, Limit = count, EnableTotalRecordCount = false,
                NextUpDateCutoff = DateTime.UtcNow.AddDays(-365), EnableRewatching = false
            }, options).Items.ToArray(), item => item.IsVisibleStandalone(user), true);
}

/// <summary>One independently requested, user-bound row.</summary>
public sealed record NativeHomeRow(string Kind, IReadOnlyList<BaseItemDto> Items);
