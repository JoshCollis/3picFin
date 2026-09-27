using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using Rowan.Jellyfin.Plugin.Configuration;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Pure, bounded projection from visible user DTOs to still-image slides.</summary>
public static class HeroPolicy
{
    /// <summary>Checks every parent up to a currently selected root; unresolved parents fail closed.</summary>
    public static bool VisibleInSelectedLibrary(BaseItem item, IEnumerable<Guid> selected,
        Func<Guid, BaseItem?> resolve, Func<BaseItem, bool> visible)
    {
        var roots = selected.ToHashSet();
        if (item.Id == Guid.Empty || !visible(item) || roots.Count == 0) return false;
        // GetAncestorIds is ordered nearest-first in Jellyfin. Refuse a missing, repeated,
        // or unexpectedly deep chain rather than silently skipping a restricted parent.
        var seen = new HashSet<Guid>();
        foreach (var id in item.GetAncestorIds().Take(65))
        {
            if (id == Guid.Empty || !seen.Add(id) || seen.Count > 64) return false;
            var parent = resolve(id);
            if (parent is null || !visible(parent)) return false;
            if (roots.Contains(id)) return parent is CollectionFolder;
        }
        return false;
    }
    public const int MaxLibraries = 20;
    public const int CandidatesPerLibrary = 20;
    public const int MaxSlides = 10;

    public static bool Enabled(PluginConfiguration? config) =>
        config?.HomeEnabled == true && config.HeroTrustedFilesystemEnabled;

    public static bool TrySelectedLibraries(PluginConfiguration? config,
        IEnumerable<MediaBrowser.Controller.Entities.CollectionFolder> visible,
        out IReadOnlyList<MediaBrowser.Controller.Entities.CollectionFolder> selected)
    {
        selected = [];
        var folders = visible.ToArray();
        if (!TrySelectLibraryIds(config?.HeroLibraryIds, folders.Select(folder => folder.Id), out var ids)) return false;
        var allowed = ids.ToHashSet();
        selected = folders.Where(folder => allowed.Contains(folder.Id)).DistinctBy(folder => folder.Id).ToArray();
        return true;
    }

    public static bool TrySelectLibraryIds(Guid[]? configured, IEnumerable<Guid> visible, out IReadOnlyList<Guid> selected)
    {
        selected = [];
        if (configured is not { Length: > 0 and <= MaxLibraries } ||
            configured.Any(id => id == Guid.Empty) || configured.Distinct().Count() != configured.Length)
            return false;
        var allowed = configured.ToHashSet();
        selected = visible.Where(allowed.Contains).Distinct().ToArray();
        return true;
    }

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
