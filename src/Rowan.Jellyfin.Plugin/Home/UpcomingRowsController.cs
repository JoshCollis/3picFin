using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Rowan.Jellyfin.Plugin.Calendar;
using Rowan.Jellyfin.Plugin.Configuration;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Arr calendar candidates, not Jellyfin items or evidence of a playable file.</summary>
public sealed record UpcomingCard(string MediaType, int TitleId, string Title, string EventType, DateTimeOffset Date,
    int? SeasonNumber, int? EpisodeNumber, string? EpisodeTitle);
public sealed record UpcomingHomeRow(string Kind, IReadOnlyList<UpcomingCard> Items, bool Partial, string? Error);

public static class UpcomingRowsPolicy
{
    public static IReadOnlyList<UpcomingCard> Select(IEnumerable<CalendarEvent> events, string kind, DateTimeOffset now)
    {
        var movie = kind == "UpcomingMovies";
        if (!movie && kind != "UpcomingShows") return [];
        var eligible = events.Where(e => e.Source == (movie ? "Radarr" : "Sonarr") &&
            e.MediaType == (movie ? "movie" : "tv") && e.Monitored == true && e.HasFile == false &&
            e.Date > now && e.Date <= now.AddDays(31) && e.TitleId > 0 &&
            (!movie || e.EventType is "Cinema" or "Digital" or "Physical") &&
            (movie || e.EventType == "Episode"));
        // Radarr can expose three release dates for one title: show the earliest future signal.
        // Sonarr episodes remain separate cards even when the series ID repeats.
        return eligible.OrderBy(e => e.Date).ThenBy(e => e.TitleId).ThenBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .DistinctBy(e => movie ? (e.TitleId, -1, -1) : (e.TitleId, e.SeasonNumber ?? -1, e.EpisodeNumber ?? -1))
            .Take(16).Select(e => new UpcomingCard(e.MediaType, e.TitleId, e.Title, e.EventType, e.Date,
                e.SeasonNumber, e.EpisodeNumber, e.EpisodeTitle)).ToArray();
    }
}

[ApiController]
[Route("Rowan/Home")]
[Authorize]
public sealed class UpcomingRowsController : ControllerBase
{
    private readonly ArrCalendarClient _client;
    private readonly Func<Guid, bool> _userExists;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly Func<DateTimeOffset> _clock;

    [ActivatorUtilitiesConstructor]
    public UpcomingRowsController(ArrCalendarClient client, IUserManager users) : this(client,
        id => users.GetUserById(id) is not null, () => Plugin.Current?.Configuration, () => DateTimeOffset.UtcNow) { }

    public UpcomingRowsController(ArrCalendarClient client, Func<Guid, bool> userExists,
        Func<PluginConfiguration?> configuration, Func<DateTimeOffset> clock)
    {
        _client = client;
        _userExists = userExists;
        _configuration = configuration;
        _clock = clock;
    }

    [HttpGet("Rows/{kind:regex(^(UpcomingMovies|UpcomingShows)$)}")]
    public async Task<ActionResult<UpcomingHomeRow>> GetRow(string kind, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (User.Identity?.IsAuthenticated != true || !RecentlyAddedPolicy.TryGetUserId(User, out var userId) || !_userExists(userId)) return Forbid();
        var config = _configuration();
        if (config?.HomeEnabled != true || !config.CalendarEnabled ||
            (kind == "UpcomingMovies" ? !config.UpcomingMoviesRowEnabled : kind == "UpcomingShows" ? !config.UpcomingShowsRowEnabled : true)) return NotFound();
        var now = _clock().ToUniversalTime();
        var start = now.UtcDateTime.Date;
        var end = start.AddDays(31);
        var calendar = await _client.GetAsync(userId, start, end, cancellationToken).ConfigureAwait(false);
        if (calendar is null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        var source = kind == "UpcomingMovies" ? calendar.Radarr : calendar.Sonarr;
        return Ok(new UpcomingHomeRow(kind, source.Error is null ? UpcomingRowsPolicy.Select(source.Items, kind, now) : [], source.Partial, source.Error));
    }
}
