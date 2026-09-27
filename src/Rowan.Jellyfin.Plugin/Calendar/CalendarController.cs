using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Rowan.Jellyfin.Plugin.Home;

namespace Rowan.Jellyfin.Plugin.Calendar;

[ApiController]
[Route("3picFin")]
[Authorize]
public sealed class CalendarController : ControllerBase
{
    private readonly ArrCalendarClient _client;
    private readonly Func<Guid, bool> _userExists;
    [ActivatorUtilitiesConstructor]
    public CalendarController(ArrCalendarClient client, IUserManager users) : this(client, id => users.GetUserById(id) is not null) { }
    public CalendarController(ArrCalendarClient client, Func<Guid, bool> userExists) { _client = client; _userExists = userExists; }

    [HttpGet("Calendar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CalendarResponse>> GetCalendar([FromQuery] string? start, [FromQuery] string? end, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (User.Identity?.IsAuthenticated != true) return Unauthorized();
        if (!RecentlyAddedPolicy.TryGetUserId(User, out var id) || !_userExists(id)) return Forbid();
        if (!ParseDay(start, out var from) || !ParseDay(end, out var until) || until <= from || until - from > TimeSpan.FromDays(31))
            return BadRequest(new { Error = "InvalidDateWindow" });
        var result = await _client.GetAsync(id, from, until, cancellationToken).ConfigureAwait(false);
        return result is null ? StatusCode(StatusCodes.Status503ServiceUnavailable) : Ok(result);
    }

    private static bool ParseDay(string? text, out DateTime date)
    {
        date = default;
        return text is { Length: 10 } && DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) && date.Year is >= 1900 and <= 2200;
    }
}
