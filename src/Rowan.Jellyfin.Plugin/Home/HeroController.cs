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

/// <summary>Authenticated, user-scoped still-image hero; no caller-selected identity or library.</summary>
[ApiController]
[Route("Rowan/Home")]
[Authorize]
public sealed class HeroController : ControllerBase
{
    private static readonly Guid PluginId = Guid.Parse("bd36ab75-0f4a-49b6-92ef-3a93da040c7a");
    private readonly IUserManager _users;
    private readonly ILibraryManager _libraries;
    private readonly IUserViewManager _views;
    private readonly IDtoService _dtos;
    private readonly IPluginManager _plugins;
    private readonly ILogger<HeroController> _logger;

    public HeroController(IUserManager users, ILibraryManager libraries, IUserViewManager views,
        IDtoService dtos, IPluginManager plugins, ILogger<HeroController> logger)
    {
        _users = users;
        _libraries = libraries;
        _views = views;
        _dtos = dtos;
        _plugins = plugins;
        _logger = logger;
    }

    [HttpGet("Hero")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<HeroSlide>> GetHero()
    {
        Response.Headers.CacheControl = "private, no-store";
        if (!RecentlyAddedPolicy.TryGetUserId(User, out var userId)) return Forbid();
        User? user = _users.GetUserById(userId);
        if (user is null) return Forbid();

        var config = (_plugins.GetPlugin(PluginId)?.Instance as Plugin)?.Configuration;
        if (!HeroPolicy.Enabled(config)) return Ok(Array.Empty<HeroSlide>());

        var visible = _libraries.GetUserRootFolder().GetChildren(user, true).OfType<CollectionFolder>();
        // This is independent of the Recently Added selection: it is a visible-library-only hero.
        if (!RecentlyAddedPolicy.TrySelectVisible(visible, null, out var libraries))
        {
            _logger.LogWarning("Hero library selection invalid; returning no slides");
            return Ok(Array.Empty<HeroSlide>());
        }

        var candidates = new List<BaseItemDto>();
        foreach (var library in libraries.Take(HeroPolicy.MaxLibraries))
        {
            var options = new DtoOptions();
            var groups = _views.GetLatestItems(new LatestItemsQuery
            {
                User = user,
                ParentId = library.Id,
                Limit = HeroPolicy.CandidatesPerLibrary,
                GroupItems = false,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                IsPlayed = null
            }, options);
            var safe = RecentlyAddedPolicy.SafeLatestItems(library, groups, _libraries.GetItemById)
                .Take(HeroPolicy.CandidatesPerLibrary).ToArray();
            // Keep DTO visibility checks enabled. Never return an image for an unchecked item.
            candidates.AddRange(_dtos.GetBaseItemDtos(safe, options, user));
        }
        return Ok(HeroPolicy.SelectSlides(candidates, userId, DateOnly.FromDateTime(DateTime.UtcNow)));
    }
}
