using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Rowan.Jellyfin.Plugin.Home;

namespace Rowan.Jellyfin.Plugin.Downloads;

[ApiController]
[Route("3picFin")]
[Authorize]
public sealed class DownloadsController : ControllerBase
{
    private readonly ArrDownloadsClient _client;
    private readonly Func<Guid, bool> _userExists;
    [ActivatorUtilitiesConstructor]
    public DownloadsController(ArrDownloadsClient client, IUserManager users) : this(client, id => users.GetUserById(id) is not null) { }
    public DownloadsController(ArrDownloadsClient client, Func<Guid, bool> userExists) { _client = client; _userExists = userExists; }

    [HttpGet("Downloads")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DownloadsResponse>> GetDownloads(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true) return Unauthorized();
        if (!RecentlyAddedPolicy.TryGetUserId(User, out var id) || !_userExists(id)) return Forbid();
        var result = await _client.GetAsync(id, cancellationToken).ConfigureAwait(false);
        Response.Headers.CacheControl = "private, no-store";
        return result is null ? StatusCode(StatusCodes.Status503ServiceUnavailable) : Ok(result);
    }
}
