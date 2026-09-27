using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Discovery;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Personally requested, locally playable Jellyfin titles, not household Seerr history.</summary>
[ApiController]
[Route("Rowan/Home/Rows")]
[Authorize]
public sealed class MyRequestsRowController : ControllerBase
{
    private static readonly Guid PluginId = Guid.Parse("bd36ab75-0f4a-49b6-92ef-3a93da040c7a");
    private readonly IUserManager _users;
    private readonly ILibraryManager _libraries;
    private readonly IDtoService _dtos;
    private readonly IPluginManager _plugins;
    private readonly IUserDataManager _userData;
    private readonly SeerrClient _seerr;

    public MyRequestsRowController(IUserManager users, ILibraryManager libraries, IDtoService dtos,
        IPluginManager plugins, IUserDataManager userData, SeerrClient seerr)
    {
        _users = users; _libraries = libraries; _dtos = dtos; _plugins = plugins; _userData = userData; _seerr = seerr;
    }

    [HttpGet("MyRequests")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<NativeHomeRow>> GetRow(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true || !RecentlyAddedPolicy.TryGetUserId(User, out var id)) return Forbid();
        User? user = _users.GetUserById(id);
        if (user is null) return Forbid();
        Response.Headers.CacheControl = "private, no-store";
        var config = (_plugins.GetPlugin(PluginId)?.Instance as Plugin)?.Configuration;
        if (config?.HomeEnabled != true || !config.MyRequestsRowEnabled) return Ok(new NativeHomeRow("MyRequests", []));
        var libraries = _libraries.GetUserRootFolder().GetChildren(user, true).OfType<CollectionFolder>()
            .Select(folder => folder.Id).Distinct().Take(64).ToArray();
        if (libraries.Length == 0) return Ok(new NativeHomeRow("MyRequests", []));
        // The mapped personal route uses X-API-User and requestedBy, never the global household feed.
        var requested = MyRequestsRowPolicy.RequestedIds(await _seerr.GetPersonalAvailableRequestsAsync(id, cancellationToken).ConfigureAwait(false));
        if (requested.Error is { } error) return StatusCode(StatusCodes.Status502BadGateway, new { Error = error });
        if (requested.Ids.Length == 0) return Ok(new NativeHomeRow("MyRequests", []));
        var candidates = requested.Ids.Select(itemId => _libraries.GetItemById<BaseItem>(itemId, user))
            .Where(item => item is not null).Cast<BaseItem>();
        var items = MyRequestsRowPolicy.Select(candidates, requested.Ids, libraries,
            item => item.IsVisibleStandalone(user), config.MyRequestsHideWatched,
            item => _userData.GetUserData(user, item)?.Played == true);
        return Ok(new NativeHomeRow("MyRequests", _dtos.GetBaseItemDtos(items, new DtoOptions(), user)));
    }
}
