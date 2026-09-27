using System;
using System.Collections.Generic;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Immutable allowlist for eligible libraries. Null means unrestricted; empty means none.</summary>
public sealed class LibrarySelection
{
    private readonly HashSet<Guid>? _ids;

    public LibrarySelection(Guid[]? configuredIds)
    {
        if (configuredIds is null)
        {
            return;
        }

        _ids = new HashSet<Guid>();
        foreach (var id in configuredIds)
        {
            if (id == Guid.Empty)
            {
                throw new ArgumentException("Library IDs cannot be empty.", nameof(configuredIds));
            }

            if (!_ids.Add(id))
            {
                throw new ArgumentException("Library IDs cannot be duplicated.", nameof(configuredIds));
            }
        }
    }

    /// <summary>Whether a library ID passes this selection (eligibility is checked by the caller).</summary>
    public bool Includes(Guid libraryId) => _ids is null || _ids.Contains(libraryId);
}
