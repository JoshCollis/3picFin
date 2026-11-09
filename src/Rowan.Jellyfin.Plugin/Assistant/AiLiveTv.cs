using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;

namespace Rowan.Jellyfin.Plugin.Assistant;

/// <summary>Read-only, user-scoped access to the imported guide, independent of Home/guide UI flags.</summary>
public sealed class AiLiveTv(IUserManager users, ILiveTvManager liveTv, ILibraryManager libraries,
    Func<BaseItem, User, bool>? visibility = null)
{
    private const int PageSize = 25;
    public bool IsAvailable(Guid id)
    {
        var user = users.GetUserById(id);
        return AiLibrary.Allowed(user) && user!.HasPermission(PermissionKind.EnableLiveTvAccess) && liveTv.IsEnabledForUser(user);
    }
    private static DtoOptions Options => new() { Fields = [], EnableImages = false, EnableUserData = false };
    private User User(Guid id)
    {
        var user = users.GetUserById(id);
        if (!AiLibrary.Allowed(user) || !user!.HasPermission(PermissionKind.EnableLiveTvAccess) || !liveTv.IsEnabledForUser(user))
            throw new AiException("Live TV is unavailable or not permitted for this account.", 403);
        return user!;
    }
    private bool Visible(BaseItem item, User user) => item.Id != Guid.Empty && (visibility?.Invoke(item, user) ?? item.IsVisibleStandalone(user));
    private LiveTvChannel? Channel(Guid id, User user) => libraries.GetItemById(id) is LiveTvChannel channel && Visible(channel, user) ? channel : null;

    public AiCard? GetChannel(Guid userId, Guid id)
    {
        var channel = Channel(id, User(userId));
        return channel is null ? null : new("channel:" + id.ToString("N"), AiJson.Clip(channel.Name, 200), "channel",
            LibraryItemId: id, Overview: "Live TV channel " + AiJson.Clip(channel.Number, 32));
    }

    public object Channels(Guid userId, string query, int page, CancellationToken token)
    {
        var user = User(userId);
        var clock = Stopwatch.StartNew();
        var matches = new List<LiveTvChannel>();
        var skip = (page - 1) * PageSize;
        var matched = 0;
        var exhausted = false;
        // Search names AND numbers across the catalogue, not the Home preview's first 1,024 channels.
        // A sentinel match provides pagination without returning a huge channel list to the model.
        for (var start = 0; start < 100_000 && clock.Elapsed < TimeSpan.FromSeconds(5); start += 500)
        {
            token.ThrowIfCancellationRequested();
            user = User(userId);
            var rows = liveTv.GetInternalChannels(new LiveTvChannelQuery
            {
                UserId = userId, StartIndex = start, Limit = 500, AddCurrentProgram = false,
                SortBy = [ItemSortBy.SortName], SortOrder = SortOrder.Ascending
            }, Options, token).Items;
            user = User(userId);
            foreach (var channel in rows.OfType<LiveTvChannel>())
            {
                if (!Visible(channel, user) || !(channel.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    channel.Number?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)) continue;
                if (matched++ >= skip) matches.Add(channel);
                if (matches.Count > PageSize) break;
            }
            if (matches.Count > PageSize) break;
            if (rows.Count < 500) { exhausted = true; break; }
        }
        user = User(userId);
        var more = matches.Count > PageSize;
        return new
        {
            source = "Jellyfin Live TV channels", items = matches.Take(PageSize).Where(c => Visible(c, user)).Select(c => new
            { key = "channel:" + c.Id.ToString("N"), channelId = c.Id, name = AiJson.Clip(c.Name, 200), number = AiJson.Clip(c.Number, 32) }).ToArray(),
            page, nextPage = more ? page + 1 : (int?)null, complete = exhausted,
            truncated = !exhausted && !more,
            note = !exhausted && !more ? "Channel search reached its safety limit. Refine the query; missing matches are not proof of absence." : null
        };
    }

    public object Guide(Guid userId, string query, Guid? channelId, string when, DateTimeOffset? start, DateTimeOffset? end,
        string type, int page, TimeZoneInfo zone, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var user = User(userId);
        if (channelId.HasValue && Channel(channelId.Value, user) is null)
            return new { error = "Channel unavailable or not visible.", complete = false };
        var folder = liveTv.GetInternalLiveTvFolder(token);
        var from = when == "window" ? start!.Value : now;
        var to = when == "now" ? now : when == "window" ? end : null;
        var filter = new InternalItemsQuery(user)
        {
            TopParentIds = [folder.Id], IncludeItemTypes = [BaseItemKind.LiveTvProgram],
            ChannelIds = channelId.HasValue ? [channelId.Value] : [],
            SearchTerm = string.IsNullOrWhiteSpace(query) ? null : query,
            MinEndDate = from.UtcDateTime, MaxStartDate = to?.UtcDateTime,
            IsMovie = type == "movie" ? true : null, IsSeries = type == "tv" ? true : null,
            IsSports = type == "sports" ? true : null, IsNews = type == "news" ? true : null, IsKids = type == "kids" ? true : null,
            StartIndex = (page - 1) * PageSize, Limit = PageSize + 1, EnableTotalRecordCount = false,
            OrderBy = [(ItemSortBy.StartDate, SortOrder.Ascending), (ItemSortBy.SortName, SortOrder.Ascending)], DtoOptions = Options
        };
        token.ThrowIfCancellationRequested();
        var rows = libraries.GetItemsResult(filter).Items;
        token.ThrowIfCancellationRequested();
        user = User(userId); // Permissions may have changed while the database was reading.
        var items = new List<object>();
        foreach (var program in rows.Take(PageSize).OfType<LiveTvProgram>())
        {
            if (!Visible(program, user) || program.EndDate is not { } finish || finish <= program.StartDate ||
                finish <= from.UtcDateTime || to.HasValue && program.StartDate > to.Value.UtcDateTime ||
                Channel(program.ChannelId, user) is not { } channel || channelId.HasValue && channel.Id != channelId.Value) continue;
            items.Add(new
            {
                programId = program.Id, channelKey = "channel:" + channel.Id.ToString("N"), channelId = channel.Id, title = AiJson.Clip(program.Name, 200),
                episodeTitle = AiJson.Clip(program.EpisodeTitle, 200), overview = AiJson.Clip(program.Overview, 500),
                channelName = AiJson.Clip(channel.Name, 200), channelNumber = AiJson.Clip(channel.Number, 32),
                startsAt = Local(program.StartDate, zone), endsAt = Local(finish, zone),
                isAiring = program.StartDate <= now.UtcDateTime && finish > now.UtcDateTime,
                program.IsMovie, program.IsSeries, program.IsSports, program.IsNews, program.IsKids, program.IsLive, program.IsRepeat
            });
        }
        User(userId);
        return new
        {
            source = "Jellyfin imported EPG", retrievedAt = now, timeZone = zone.Id,
            from = TimeZoneInfo.ConvertTime(from, zone), to = to.HasValue ? TimeZoneInfo.ConvertTime(to.Value, zone) : (DateTimeOffset?)null,
            items, page, nextPage = rows.Count > PageSize ? page + 1 : (int?)null, complete = rows.Count <= PageSize,
            note = "Only imported listings are searchable. No match is not proof a programme will not air; the provider may not have supplied that date. Times include UTC offsets. Listings do not guarantee playback."
        };
    }

    private static DateTimeOffset Local(DateTime date, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)), zone);

    public static DateTimeOffset? Date(string? value)
    {
        if (value is null) return null;
        // Never interpret a model's offset-free date using the server's local time zone.
        if (value.Length < 20 || !(value.EndsWith('Z') || value.Length >= 6 && value[^3] == ':' && value[^6] is '+' or '-') ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new FormatException();
        return date;
    }
}
