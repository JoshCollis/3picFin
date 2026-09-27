using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Authenticated, user-scoped Home rows.</summary>
[ApiController]
[Route("Rowan/Home")]
[Authorize]
public sealed class RecentlyAddedController : ControllerBase
{
    private static readonly Guid PluginId = Guid.Parse("bd36ab75-0f4a-49b6-92ef-3a93da040c7a");
    private readonly IUserManager _users;
    private readonly ILibraryManager _libraries;
    private readonly IUserViewManager _views;
    private readonly IDtoService _dtos;
    private readonly IPluginManager _plugins;
    private readonly ILogger<RecentlyAddedController> _logger;

    /// <summary>Constructs the endpoint from Jellyfin services.</summary>
    public RecentlyAddedController(IUserManager users, ILibraryManager libraries, IUserViewManager views,
        IDtoService dtos, IPluginManager plugins, ILogger<RecentlyAddedController> logger)
    {
        _users = users;
        _libraries = libraries;
        _views = views;
        _dtos = dtos;
        _plugins = plugins;
        _logger = logger;
    }

    /// <summary>Returns at most twenty recent items per visible selected library.</summary>
    [HttpGet("RecentlyAdded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<RecentlyAddedRow>> GetRecentlyAdded()
    {
        // Jellyfin also authorizes unrestricted API keys; they have no user identity.
        if (!RecentlyAddedPolicy.TryGetUserId(User, out var userId))
        {
            return Forbid();
        }

        User? user = _users.GetUserById(userId);
        if (user is null)
        {
            return Forbid();
        }

        var config = (_plugins.GetPlugin(PluginId)?.Instance as Plugin)?.Configuration;
        if (config?.HomeEnabled != true)
        {
            return Ok(Array.Empty<RecentlyAddedRow>());
        }

        var visible = _libraries.GetUserRootFolder().GetChildren(user, true).OfType<CollectionFolder>();
        if (!RecentlyAddedPolicy.TrySelectVisible(visible, config.RecentlyAddedLibraryIds, out var selected))
        {
            _logger.LogWarning("Recently Added library selection is invalid; returning no rows");
            return Ok(Array.Empty<RecentlyAddedRow>());
        }
        var rows = new List<RecentlyAddedRow>(selected.Count);
        foreach (var library in selected)
        {
            var dtoOptions = new DtoOptions();
            var groups = _views.GetLatestItems(new LatestItemsQuery
            {
                User = user,
                ParentId = library.Id,
                Limit = 20,
                GroupItems = false,
                IncludeItemTypes = Array.Empty<BaseItemKind>(),
                IsPlayed = user.HidePlayedInLatest ? false : null
            }, dtoOptions);
            var items = RecentlyAddedPolicy.SafeLatestItems(library, groups, _libraries.GetItemById);
            var dtos = _dtos.GetBaseItemDtos(items, dtoOptions, user);
            rows.Add(new RecentlyAddedRow(library.Id, library.Name, dtos));
        }

        return Ok(rows);
    }
}

/// <summary>A library's recent items.</summary>
public sealed record RecentlyAddedRow(Guid LibraryId, string LibraryName, IReadOnlyList<BaseItemDto> Items);
