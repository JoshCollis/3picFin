using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using Rowan.Jellyfin.Plugin.Configuration;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Pure, bounded projection from visible user DTOs to still-image slides.</summary>
public static class HeroPolicy
{
    public const int MaxLibraries = 20;
    public const int CandidatesPerLibrary = 20;
    public const int MaxSlides = 12;

    public static bool Enabled(PluginConfiguration? config) =>
        config?.HomeEnabled == true && config.HeroTrustedFilesystemEnabled;

    public static IReadOnlyList<HeroSlide> SelectSlides(IEnumerable<BaseItemDto> candidates, Guid userId, DateOnly day)
    {
        var seed = Encoding.UTF8.GetBytes($"{userId:N}:{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        return candidates.Where(item => item.Type is BaseItemKind.Movie or BaseItemKind.Series
                && item.BackdropImageTags is { Length: > 0 } && item.Id != Guid.Empty)
            .DistinctBy(item => item.Id)
            // Stable per-user/day ordering without exposing a client-controlled shuffle seed.
            .OrderBy(item => Convert.ToHexString(SHA256.HashData(seed.Concat(item.Id.ToByteArray()).ToArray())), StringComparer.Ordinal)
            .Take(MaxSlides)
            .Select(item => new HeroSlide(item.Id, item.Name, item.Overview, item.ProductionYear,
                "Backdrop", 0, item.BackdropImageTags![0]))
            .ToArray();
    }
}

/// <summary>Only first-party image identity; clients construct bounded image routes via ApiClient.</summary>
public sealed record HeroSlide(Guid Id, string? Name, string? Overview, int? ProductionYear,
    string ImageType, int ImageIndex, string ImageTag);
