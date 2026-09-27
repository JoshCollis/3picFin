using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Independent, default-off per-user Because You Watched Home data.</summary>
[ApiController]
[Route("Rowan/Home")]
[Authorize]
public sealed class BecauseYouWatchedController : ControllerBase
{
    private static readonly Guid PluginId = Guid.Parse("bd36ab75-0f4a-49b6-92ef-3a93da040c7a");
    private readonly IUserManager _users;
    private readonly ILibraryManager _libraries;
    private readonly ISimilarItemsManager _similar;
    private readonly IDtoService _dtos;
    private readonly IPluginManager _plugins;
    private readonly IUserDataManager _data;
    private readonly ICollectionManager _collections;

    /// <summary>Constructs the user-bound endpoint.</summary>
    public BecauseYouWatchedController(IUserManager users, ILibraryManager libraries,
        ISimilarItemsManager similar, IDtoService dtos, IPluginManager plugins,
        IUserDataManager data, ICollectionManager collections)
    {
        _users = users; _libraries = libraries; _similar = similar; _dtos = dtos;
        _plugins = plugins; _data = data; _collections = collections;
    }

    /// <summary>Returns up to five independently identified seed headings and sixteen cards each.</summary>
    [HttpGet("BecauseYouWatched")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<BecauseYouWatchedRow>>> GetRows(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RecentlyAddedPolicy.TryGetUserId(User, out var userId)) return Forbid();
        var user = _users.GetUserById(userId);
        if (user is null) return Forbid();
        Response.Headers.CacheControl = "private, no-store";
        var config = (_plugins.GetPlugin(PluginId)?.Instance as Plugin)?.Configuration;
        if (config?.HomeEnabled != true || !config.BecauseYouWatchedRowEnabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Ok(Array.Empty<BecauseYouWatchedRow>());
        }

        Guid[] VisibleLibraries() => _libraries.GetUserRootFolder().GetChildren(user, true)
            .OfType<CollectionFolder>()
            .Where(folder => folder.CollectionType is null or CollectionType.movies)
            .Select(folder => folder.Id).Distinct().Take(8).ToArray();
        var ids = VisibleLibraries();
        if (ids.Length == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Ok(Array.Empty<BecauseYouWatchedRow>());
        }
        // Check every intermediate folder/series as well as the item. DTO visibility alone does not
        // reject a child beneath a rating-restricted parent on this Jellyfin release.
        bool Visible(BaseItem item)
        {
            if (!item.IsVisibleStandalone(user)) return false;
            var ancestors = item.GetAncestorIds().ToArray();
            if (!ancestors.Any(ids.Contains)) return false;
            foreach (var parentId in ancestors.TakeWhile(id => !ids.Contains(id)))
            {
                var parent = _libraries.GetItemById(parentId);
                if (parent is null || !parent.IsVisibleStandalone(user)) return false;
            }
            return true;
        }
        IReadOnlyCollection<Guid> Collections(Movie movie) => _collections.GetCollectionsContainingItem(user, movie.Id)
            .Where(box => box.IsVisibleStandalone(user)).Select(box => box.Id).Distinct().Take(33).ToArray();
        var options = new DtoOptions();
        var seeds = BecauseYouWatchedPolicy.CollectSeeds(ids, (library, start, count) =>
            _libraries.GetItemsResult(new InternalItemsQuery(user)
            {
                ParentId = library, Recursive = true, IncludeItemTypes = [BaseItemKind.Movie],
                IsPlayed = true, IsVirtualItem = false,
                OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending), (ItemSortBy.Random, SortOrder.Descending)],
                StartIndex = start, Limit = count, EnableTotalRecordCount = false
            }).Items.ToArray(), item => Visible(item) && _data.GetUserData(user, item)?.Played == true,
            Collections, 15, 5);
        Guid[] CurrentLibraries()
        {
            user = _users.GetUserById(userId);
            return user is null ? [] : VisibleLibraries();
        }
        bool CurrentSeedVisible(BaseItem item) => user is not null && Visible(item);
        // Jellyfin 12.1 runs local providers with null LibraryOptions; no remote provider is opted in.
        // It can return candidates outside the user's roots when its access-filter root set is empty.
        var gathered = await BecauseYouWatchedPolicy.GatherResultsAsync(seeds, CurrentLibraries,
            CurrentSeedVisible, item => user is not null && _data.GetUserData(user, item)?.Played == true,
            async (seed, token) => await _similar.GetSimilarItemsAsync(seed, [], user!,
                new DtoOptions(), 64, null, token).ConfigureAwait(false),
            16, config.BecauseYouWatchedHideWatched, cancellationToken).ConfigureAwait(false);
        var finalized = BecauseYouWatchedPolicy.FinalizeRows(gathered, CurrentLibraries,
            CurrentSeedVisible, item => user is not null && _data.GetUserData(user, item)?.Played == true,
            item => _dtos.GetBaseItemDto(item, options, user!),
            item => _dtos.GetBaseItemDto(item, options, user!), cancellationToken);
        var rows = finalized.Select(row => new BecauseYouWatchedRow(row.Seed.Id, row.SeedDto,
            "Because You Watched " + row.Seed.Name, row.Items)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return Ok(rows);
    }
}

/// <summary>Seed-specific heading and card identity, not a flattened recommendation pool.</summary>
public sealed record BecauseYouWatchedRow(Guid SeedId, BaseItemDto Seed, string Heading, IReadOnlyList<BaseItemDto> Items);
