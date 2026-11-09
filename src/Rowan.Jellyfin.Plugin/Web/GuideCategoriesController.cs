using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Home;

namespace Rowan.Jellyfin.Plugin.Web;

public sealed record GuideCategory(string Id, string Name, string[] ChannelIds);
public sealed record GuideCategories(bool Available, string Source, GuideCategory[] Items, string? Status = null);
public sealed record GuideCategoryChannel(Guid Id, string ExternalId);
public sealed record GuideAssignedGroup(string ExternalId, string Name, string? GroupKey = null);

/// <summary>Exact native external-ID join, never names, prefixes, programme genres or arbitrary Tags.</summary>
public static class GuideCategoryPolicy
{
    public const int MaximumChannels = 10000;
    public const string CombinedSource = "jellyfin-assigned-channel-groups";
    public static GuideCategories Unavailable(string status = "unavailable") => new(false, "jellyfin-m3u-group-title-cache", [], status);

    public static GuideCategories Select(IEnumerable<GuideCategoryChannel> channels, IEnumerable<GuideAssignedGroup> assignments, string source = "jellyfin-m3u-group-title-cache")
    {
        var visible = channels.Take(MaximumChannels + 1).ToArray();
        var rows = assignments.Take(MaximumChannels + 1).ToArray();
        if (visible.Length > MaximumChannels || rows.Length > MaximumChannels ||
            visible.Any(c => c.Id == Guid.Empty || string.IsNullOrEmpty(c.ExternalId)) ||
            visible.Select(c => c.Id).Distinct().Count() != visible.Length ||
            visible.Select(c => c.ExternalId).Distinct(StringComparer.Ordinal).Count() != visible.Length ||
            rows.Any(r => string.IsNullOrEmpty(r.ExternalId) || r.Name.Length > 200 ||
                r.Name.Contains("://", StringComparison.Ordinal) || r.Name.Any(char.IsControl)) ||
            rows.Select(r => r.ExternalId).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            return Unavailable();
        var allowed = visible.ToDictionary(c => c.ExternalId, c => c.Id.ToString("N"), StringComparer.Ordinal);
        var groups = rows.Where(r => !string.IsNullOrWhiteSpace(r.Name) && allowed.ContainsKey(r.ExternalId))
            .GroupBy(r => (r.Name, r.GroupKey)).ToArray();
        if (groups.Length > 256) return Unavailable();
        return new(true, source, groups.OrderBy(g => g.Key.Name, StringComparer.Ordinal).ThenBy(g => g.Key.GroupKey, StringComparer.Ordinal)
            .Select(g => new GuideCategory("group-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(g.Key.GroupKey ?? g.Key.Name))),
                g.Key.Name, g.Select(r => allowed[r.ExternalId]).Order(StringComparer.Ordinal).ToArray())).ToArray());
    }

    /// <summary>Read only the configured tuner's already-imported inventory; no tuner URL or stream is opened.</summary>
    public static async Task<GuideAssignedGroup[]> ReadCache(string cachePath, TunerHostInfo[] hosts, CancellationToken cancellation)
        => await ReadInventory(cachePath, hosts, "m3u", row => new GuideAssignedGroup(row.GetProperty("Id").GetString() ?? "",
            row.TryGetProperty("ChannelGroup", out var group) ? group.GetString() ?? "" : ""), cancellation).ConfigureAwait(false);

    public static Task<DispatcharrCacheChannel[]> ReadDispatcharrCache(string cachePath, TunerHostInfo[] hosts, CancellationToken cancellation)
        => ReadInventory(cachePath, hosts, "hdhomerun", row => {
            if (!DispatcharrIdentity.TryRead(row.GetProperty("Path").GetString(), out var uuid)) throw new InvalidDataException();
            return new DispatcharrCacheChannel(row.GetProperty("Id").GetString() ?? "", uuid);
        }, cancellation);

    private static async Task<T[]> ReadInventory<T>(string cachePath, TunerHostInfo[] hosts, string type, Func<JsonElement,T> project, CancellationToken cancellation)
    {
        var tuners = hosts.Where(h => string.Equals(h.Type, type, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (tuners.Length is 0 or > 8 || tuners.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != tuners.Length)
            throw new InvalidDataException();
        var rows = new List<T>();
        long remaining = 16 * 1024 * 1024;
        foreach (var host in tuners)
        {
            // Native IDs are GUIDs. Reject path components rather than trusting a configured filename.
            if (!Guid.TryParse(host.Id, out var id) || id == Guid.Empty || host.Id.IndexOfAny(['/', '\\']) >= 0)
                throw new InvalidDataException();
            await using var stream = new FileStream(Path.Combine(cachePath, host.Id + "_channels"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var size = stream.Length;
            if (size < 2 || size > remaining) throw new InvalidDataException();
            remaining -= size;
            var bytes = new byte[(int)size];
            await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
            if (stream.Length != size) throw new InvalidDataException();
            using var document = ParseNativeCache(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (rows.Count >= MaximumChannels || row.ValueKind != JsonValueKind.Object ||
                    row.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != row.EnumerateObject().Count() ||
                    row.GetProperty("TunerHostId").GetString() != host.Id)
                    throw new InvalidDataException();
                rows.Add(project(row));
            }
        }
        return rows.ToArray();
    }

    // Native OpenWrite does not always truncate an older, longer cache. Consume
    // exactly its first JSON value; bounded trailing remnants are never inspected.
    public static JsonDocument ParseNativeCache(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
        return JsonDocument.ParseValue(ref reader);
    }
}

/// <summary>Private navigation metadata only, scoped to an existing user's current Live TV visibility.</summary>
[ApiController]
[Authorize]
[Route("3picFin/Guide/Categories")]
public sealed class GuideCategoriesController(IUserManager users, ILiveTvManager liveTv, IServerConfigurationManager configuration,
    DispatcharrSettingsStore source, DispatcharrClient metadata, Func<bool>? featureEnabled = null) : ControllerBase
{
    private bool Enabled() => featureEnabled?.Invoke() ?? GuidePerformanceTransform.Enabled(Plugin.Current?.Configuration);
    [HttpGet]
    public async Task<ActionResult<GuideCategories>> Get()
    {
        Response.Headers.CacheControl = "private, no-store";
        if (User.Identity?.IsAuthenticated != true) return Unauthorized();
        if (!RecentlyAddedPolicy.TryGetUserId(User, out var id) || users.GetUserById(id) is not { } user ||
            !liveTv.IsEnabledForUser(user)) return Forbid();
        if (!Enabled()) return NotFound();
        try
        {
            var settings = source.Snapshot();
            DispatcharrMetadata? snapshot = null;
            var before = (LiveTvOptions)configuration.GetConfiguration("livetv");
            if (settings.Enabled && before.TunerHosts.Any(h => string.Equals(h.Type,"hdhomerun",StringComparison.OrdinalIgnoreCase)))
                snapshot = await metadata.Read(settings,HttpContext.RequestAborted).ConfigureAwait(false);
            // No cached per-user response: recheck permission, settings, topology and channel visibility AFTER upstream I/O.
            if (!source.IsCurrent(settings)) return Ok(GuideCategoryPolicy.Unavailable("settings-changed"));
            if (users.GetUserById(id) is not { } current || !liveTv.IsEnabledForUser(current)) return Forbid();
            if (!Enabled()) return NotFound();
            var options = (LiveTvOptions)configuration.GetConfiguration("livetv");
            var topology = Topology(options.TunerHosts);
            if (!options.TunerHosts.Any(h => string.Equals(h.Type,"m3u",StringComparison.OrdinalIgnoreCase) ||
                settings.Enabled && string.Equals(h.Type,"hdhomerun",StringComparison.OrdinalIgnoreCase)))
                return Ok(GuideCategoryPolicy.Unavailable("not-configured"));
            var rows = new List<GuideAssignedGroup>();
            if (options.TunerHosts.Any(h => string.Equals(h.Type,"m3u",StringComparison.OrdinalIgnoreCase)))
                rows.AddRange(await GuideCategoryPolicy.ReadCache(configuration.ApplicationPaths.CachePath, options.TunerHosts,HttpContext.RequestAborted).ConfigureAwait(false));
            if (settings.Enabled && options.TunerHosts.Any(h => string.Equals(h.Type,"hdhomerun",StringComparison.OrdinalIgnoreCase)))
            {
                if (snapshot is null) return Ok(GuideCategoryPolicy.Unavailable("settings-changed"));
                rows.AddRange(DispatcharrIdentity.Assign(await GuideCategoryPolicy.ReadDispatcharrCache(configuration.ApplicationPaths.CachePath,
                    options.TunerHosts,HttpContext.RequestAborted).ConfigureAwait(false),snapshot));
            }
            if (!source.IsCurrent(settings) || topology != Topology(((LiveTvOptions)configuration.GetConfiguration("livetv")).TunerHosts))
                return Ok(GuideCategoryPolicy.Unavailable("settings-changed"));
            if (users.GetUserById(id) is not { } latest || !liveTv.IsEnabledForUser(latest)) return Forbid();
            if (!Enabled()) return NotFound();
            return Ok(GuideCategoryPolicy.Select(Visible(id,latest), rows, settings.Enabled ? GuideCategoryPolicy.CombinedSource : "jellyfin-m3u-group-title-cache"));
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            // Never return/log cache contents, exception text, tuner configuration or provider addresses.
            return Ok(GuideCategoryPolicy.Unavailable());
        }
    }

    private static string Topology(TunerHostInfo[] hosts) => string.Join("|",hosts.Select(h => h.Type + ":" + h.Id).Order(StringComparer.Ordinal));

    private GuideCategoryChannel[] Visible(Guid id, global::Jellyfin.Database.Implementations.Entities.User user)
    {
        var channels = new List<GuideCategoryChannel>(); int? total = null;
        for (var start = 0; start < GuideCategoryPolicy.MaximumChannels; start += 500)
        {
            var result = liveTv.GetInternalChannels(new LiveTvChannelQuery { UserId=id, StartIndex=start, Limit=500 },new DtoOptions(),HttpContext.RequestAborted);
            if (result.TotalRecordCount < start || result.TotalRecordCount > GuideCategoryPolicy.MaximumChannels ||
                total.HasValue && total != result.TotalRecordCount || result.Items.Count != Math.Min(500,result.TotalRecordCount-start)) throw new InvalidDataException();
            total=result.TotalRecordCount;
            channels.AddRange(result.Items.OfType<LiveTvChannel>().Where(c=>c.ServiceName=="Emby" && c.IsVisibleStandalone(user)).Select(c=>new GuideCategoryChannel(c.Id,c.ExternalId)));
            if (start+result.Items.Count>=total) break;
        }
        return channels.ToArray();
    }
}
