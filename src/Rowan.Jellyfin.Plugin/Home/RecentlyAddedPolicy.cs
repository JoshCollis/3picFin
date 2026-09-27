using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using MediaBrowser.Controller.Entities;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Limits Recently Added to the authenticated user's visible collection folders.</summary>
public static class RecentlyAddedPolicy
{
    /// <summary>Only a nonempty Jellyfin user claim can identify a caller.</summary>
    public static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        userId = Guid.Empty;
        var claims = principal.FindAll("Jellyfin-UserId").ToArray();
        return claims.Length == 1 && Guid.TryParse(claims[0].Value, out userId) && userId != Guid.Empty;
    }

    /// <summary>Validates persisted configuration without allowing malformed IDs to widen access.</summary>
    public static bool TrySelectVisible(
        IEnumerable<CollectionFolder> visibleLibraries,
        Guid[]? configuredIds,
        out IReadOnlyList<CollectionFolder> selected)
    {
        selected = [];
        LibrarySelection selection;
        try
        {
            selection = new LibrarySelection(configuredIds);
        }
        catch (ArgumentException)
        {
            return false;
        }

        selected = visibleLibraries.Where(library => selection.Includes(library.Id))
            .DistinctBy(library => library.Id).ToArray();
        return true;
    }

    /// <summary>Checks the parent after the query and excludes any foreign or empty groups.</summary>
    public static BaseItem[] SafeLatestItems(
        CollectionFolder library,
        IEnumerable<Tuple<BaseItem, List<BaseItem>>> groups,
        Func<Guid, BaseItem?> resolveParent)
    {
        // Jellyfin falls back to all user roots if ParentId disappears during GetLatestItems.
        if (resolveParent(library.Id) is not CollectionFolder parent || parent.Id != library.Id)
        {
            return [];
        }

        return groups.Where(group => group.Item2 is { Count: > 0 })
            .Select(group => group.Item2[0])
            .Where(item => item.GetAncestorIds().Contains(library.Id))
            .ToArray();
    }

    /// <summary>Intersects the user's visible libraries with the configured allowlist.</summary>
    public static IReadOnlyList<CollectionFolder> SelectVisible(
        IEnumerable<CollectionFolder> visibleLibraries,
        Guid[]? configuredIds,
        bool homeEnabled = true)
    {
        if (!homeEnabled)
        {
            return [];
        }

        var selection = new LibrarySelection(configuredIds);
        return visibleLibraries.Where(library => selection.Includes(library.Id))
            .DistinctBy(library => library.Id).ToArray();
    }
}
