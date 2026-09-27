using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using Rowan.Jellyfin.Plugin.Discovery;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Pure, fail-closed selection for locally present personal requests.</summary>
public static class MyRequestsRowPolicy
{
    public sealed record RequestedMedia(Guid[] Ids, string? Error);

    public static RequestedMedia RequestedIds(SeerrResult source)
    {
        if (source.Failure is { } failure) return new([], failure.ToString());
        if (source.Value is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() > 100)
            return new([], SeerrFailure.UpstreamUnavailable.ToString());
        var ids = new HashSet<Guid>();
        foreach (var request in results.EnumerateArray())
        {
            if (request.ValueKind != JsonValueKind.Object) return new([], SeerrFailure.UpstreamUnavailable.ToString());
            if (!request.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var state) || state is not (2 or 5) ||
                !request.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object ||
                !media.TryGetProperty("status", out var mediaStatus) || mediaStatus.ValueKind != JsonValueKind.Number || !mediaStatus.TryGetInt32(out var available) || available is not (4 or 5) ||
                !media.TryGetProperty("jellyfinMediaId", out var itemId) || itemId.ValueKind != JsonValueKind.String) continue;
            var text = itemId.GetString();
            if (Guid.TryParseExact(text, "D", out var id) || Guid.TryParseExact(text, "N", out id))
                if (id != Guid.Empty) ids.Add(id);
        }
        return new(ids.ToArray(), null);
    }

    public static BaseItem[] Select(IEnumerable<BaseItem> candidates, IReadOnlyCollection<Guid> requestedIds,
        IReadOnlyCollection<Guid> visibleLibraries, Func<BaseItem, bool> isVisible, bool hideWatched, Func<BaseItem, bool> played)
    {
        if (requestedIds.Count == 0 || visibleLibraries.Count == 0) return [];
        var requested = requestedIds.ToHashSet(); var libraries = visibleLibraries.ToHashSet();
        return candidates.Where(item => item.Id != Guid.Empty && !item.IsVirtualItem && requested.Contains(item.Id) &&
            item.GetAncestorIds().Any(libraries.Contains) && isVisible(item) && (!hideWatched || !played(item)))
            .DistinctBy(item => item.Id).OrderByDescending(item => item.DateCreated).ThenBy(item => item.Id).Take(16).ToArray();
    }
}
