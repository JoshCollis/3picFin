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
public sealed record GuideCategories(bool Available, string Source, GuideCategory[] Items);
public sealed record GuideCategoryChannel(Guid Id, string ExternalId);
public sealed record GuideAssignedGroup(string ExternalId, string Name);

/// <summary>Exact native external-ID join, never names, prefixes, programme genres or arbitrary Tags.</summary>
public static class GuideCategoryPolicy
{
    public const int MaximumChannels = 10000;
    public static GuideCategories Unavailable() => new(false, "jellyfin-m3u-group-title-cache", []);

    public static GuideCategories Select(IEnumerable<GuideCategoryChannel> channels, IEnumerable<GuideAssignedGroup> assignments)
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
            .GroupBy(r => r.Name, StringComparer.Ordinal).ToArray();
        if (groups.Length > 256) return Unavailable();
        return new(true, "jellyfin-m3u-group-title-cache", groups.OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new GuideCategory("group-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(g.Key))),
                g.Key, g.Select(r => allowed[r.ExternalId]).Order(StringComparer.Ordinal).ToArray())).ToArray());
    }

    /// <summary>Read only the configured tuner's already-imported inventory; no tuner URL or stream is opened.</summary>
    public static async Task<GuideAssignedGroup[]> ReadCache(string cachePath, TunerHostInfo[] hosts, CancellationToken cancellation)
    {
        var tuners = hosts.Where(h => string.Equals(h.Type, "m3u", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (tuners.Length is 0 or > 8 || tuners.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != tuners.Length)
            throw new InvalidDataException();
        var rows = new List<GuideAssignedGroup>();
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
                rows.Add(new GuideAssignedGroup(row.GetProperty("Id").GetString() ?? "",
                    row.TryGetProperty("ChannelGroup", out var group) ? group.GetString() ?? "" : ""));
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
public sealed class GuideCategoriesController(IUserManager users, ILiveTvManager liveTv, IServerConfigurationManager configuration) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<GuideCategories>> Get()
    {
        Response.Headers.CacheControl = "private, no-store";
        if (User.Identity?.IsAuthenticated != true) return Unauthorized();
        if (!RecentlyAddedPolicy.TryGetUserId(User, out var id) || users.GetUserById(id) is not { } user ||
            !liveTv.IsEnabledForUser(user)) return Forbid();
        if (!GuidePerformanceTransform.Enabled(Plugin.Current?.Configuration)) return NotFound();
        try
        {
            var channels = new List<GuideCategoryChannel>();
            int? total = null;
            for (var start = 0; start < GuideCategoryPolicy.MaximumChannels; start += 500)
            {
                var result = liveTv.GetInternalChannels(new LiveTvChannelQuery { UserId = id, StartIndex = start, Limit = 500 },
                    new DtoOptions(), HttpContext.RequestAborted);
                if (result.TotalRecordCount < 0 || result.TotalRecordCount > GuideCategoryPolicy.MaximumChannels ||
                    total.HasValue && total != result.TotalRecordCount || result.Items.Count != Math.Min(500, result.TotalRecordCount - start))
                    return Ok(GuideCategoryPolicy.Unavailable());
                total = result.TotalRecordCount;
                channels.AddRange(result.Items.OfType<LiveTvChannel>().Where(c => c.ServiceName == "Emby" && c.IsVisibleStandalone(user))
                    .Select(c => new GuideCategoryChannel(c.Id, c.ExternalId)));
                if (start + result.Items.Count >= total) break;
            }
            var options = (LiveTvOptions)configuration.GetConfiguration("livetv");
            var rows = await GuideCategoryPolicy.ReadCache(configuration.ApplicationPaths.CachePath, options.TunerHosts,
                HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(GuideCategoryPolicy.Select(channels, rows));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            // Never return/log cache contents, exception text, tuner configuration or provider addresses.
            return Ok(GuideCategoryPolicy.Unavailable());
        }
    }
}
