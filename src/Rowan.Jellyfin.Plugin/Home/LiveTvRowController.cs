using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Only first-party program/channel identities and labels; never stream URLs or tuner data.</summary>
public sealed record LiveTvCard(Guid ProgramId, Guid ChannelId, string Title, string ChannelName, string? ChannelNumber, DateTime StartDate, DateTime EndDate);

/// <summary>Intersect the recommended guide with the user's current channel inventory and parental visibility.</summary>
public static class LiveTvRowPolicy
{
    /// <summary>Returns at most 24 currently airing, visible programs on entitled channels.</summary>
    public static LiveTvCard[] Select(IEnumerable<LiveTvProgram> programs, IEnumerable<LiveTvChannel> channels,
        Func<MediaBrowser.Controller.Entities.BaseItem, bool> isVisible, DateTime nowUtc)
    {
        static string Label(string? value, string fallback, int limit) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Length <= limit ? value : value[..limit];
        var allowed = channels.Take(1024).Where(channel => channel.Id != Guid.Empty && isVisible(channel))
            .DistinctBy(channel => channel.Id).ToDictionary(channel => channel.Id);
        var seen = new HashSet<Guid>();
        return programs.Take(200).Where(program => program.Id != Guid.Empty && seen.Add(program.Id) &&
                program.ChannelId != Guid.Empty && allowed.ContainsKey(program.ChannelId) &&
                program.StartDate <= nowUtc && program.EndDate > nowUtc && isVisible(program))
            .Take(24).Select(program => new LiveTvCard(program.Id, program.ChannelId,
                Label(program.Name, "Live program", 160),
                Label(allowed[program.ChannelId].Name, "Live TV channel", 160),
                allowed[program.ChannelId].Number is { } number ? Label(number, "", 16) : null,
                program.StartDate, program.EndDate!.Value)).ToArray();
    }
}

/// <summary>Separate default-off Live TV Home candidate, without caller identity, tuner, channel or limit input.</summary>
[ApiController]
[Route("Rowan/Home")]
[Authorize]
public sealed class LiveTvRowController : ControllerBase
{
    private static readonly Guid PluginId = Guid.Parse("bd36ab75-0f4a-49b6-92ef-3a93da040c7a");
    private readonly IUserManager _users;
    private readonly ILiveTvManager _liveTv;
    private readonly IPluginManager _plugins;

    /// <summary>Creates a Live TV row controller.</summary>
    public LiveTvRowController(IUserManager users, ILiveTvManager liveTv, IPluginManager plugins)
    {
        _users = users;
        _liveTv = liveTv;
        _plugins = plugins;
    }

    /// <summary>Returns an authorized guide preview, never a playable stream.</summary>
    [HttpGet("Rows/LiveTV")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<LiveTvHomeRow> GetRow()
    {
        if (!RecentlyAddedPolicy.TryGetUserId(User, out var userId)) return Forbid();
        User? user = _users.GetUserById(userId);
        if (user is null) return Forbid();
        Response.Headers.CacheControl = "private, no-store";
        var config = (_plugins.GetPlugin(PluginId)?.Instance as Plugin)?.Configuration;
        if (config?.HomeEnabled != true || !config.LiveTvRowEnabled || !_liveTv.IsEnabledForUser(user))
            return Ok(new LiveTvHomeRow("LiveTV", []));

        var options = new DtoOptions();
        var channels = new List<LiveTvChannel>();
        for (var page = 0; page < 8; page++)
        {
            var batch = _liveTv.GetInternalChannels(new LiveTvChannelQuery { UserId = user.Id,
                StartIndex = page * 128, Limit = 128 }, options, HttpContext.RequestAborted).Items;
            channels.AddRange(batch.OfType<LiveTvChannel>());
            if (batch.Count < 128) break;
        }
        if (channels.Count == 0) return Ok(new LiveTvHomeRow("LiveTV", []));
        // Same recommendation ranking as HSS, but request up to 200 results so
        // entitlement filtering can refill; Jellyfin 12.1 scans at most 800 internally.
        var programs = _liveTv.GetRecommendedProgramsInternal(new InternalItemsQuery(user)
        {
            User = user, Limit = 200, IsAiring = true, EnableTotalRecordCount = false
        }, options, HttpContext.RequestAborted).Items.OfType<LiveTvProgram>();
        return Ok(new LiveTvHomeRow("LiveTV", LiveTvRowPolicy.Select(programs, channels,
            item => item.IsVisibleStandalone(user), DateTime.UtcNow)));
    }
}

/// <summary>A bounded, private Live TV guide candidate.</summary>
public sealed record LiveTvHomeRow(string Kind, IReadOnlyList<LiveTvCard> Items);
