using System;
using MediaBrowser.Model.Plugins;

namespace Rowan.Jellyfin.Plugin.Configuration;

/// <summary>Saved configuration. A null library selection means all eligible libraries; an empty array means none.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Enable portable Home integration on new installs. Existing saved choices remain unchanged.</summary>
    public bool HomeEnabled { get; set; } = true;

    /// <summary>Opt-in supplementary native-looking Home rows; never replaces Jellyfin's own sections.</summary>
    public bool NativeHomeRowsEnabled { get; set; }

    /// <summary>Ordered, explicitly selected row kinds. Null/empty renders no supplementary rows.</summary>
    public string[]? NativeHomeRowKinds { get; set; }

    /// <summary>Opt-in combined playback row; requires Home enabled.</summary>
    public bool CombinedPlaybackRowEnabled { get; set; }

    /// <summary>Independent HSS Discover (trending) Seerr candidate feed. Requires Home and Seerr; default off.</summary>
    public bool DiscoverRowEnabled { get; set; }

    /// <summary>Independent HSS DiscoverMovies Seerr candidate feed. Requires Home and Seerr; default off.</summary>
    public bool DiscoverMoviesRowEnabled { get; set; }

    /// <summary>Independent HSS DiscoverTV Seerr candidate feed. Requires Home and Seerr; default off.</summary>
    public bool DiscoverTvRowEnabled { get; set; }

    /// <summary>Hide already-played items in the combined row, matching HSS's optional filter.</summary>
    public bool CombinedPlaybackHideWatched { get; set; }

    /// <summary>Opt-in personal, locally available Seerr requests Home row; requires Home and Seerr.</summary>
    public bool MyRequestsRowEnabled { get; set; }

    /// <summary>Hide watched local items from the personal requests row.</summary>
    public bool MyRequestsHideWatched { get; set; }

    /// <summary>Opt-in collection cards; requires Home enabled.</summary>
    public bool CollectionsRowEnabled { get; set; }

    /// <summary>Opt-in Live TV guide preview; requires Home and the user's Live TV grant.</summary>
    public bool LiveTvRowEnabled { get; set; }

    /// <summary>Independently opt in to per-user Because You Watched data; requires Home.</summary>
    public bool BecauseYouWatchedRowEnabled { get; set; }

    /// <summary>Hide already watched similar cards, matching the HSS per-section option.</summary>
    public bool BecauseYouWatchedHideWatched { get; set; }

    /// <summary>Explicit opt-in: media and metadata files must be writable only by trusted server operators. Disabled by default.</summary>
    public bool HeroTrustedFilesystemEnabled { get; set; }

    /// <summary>Explicit hero library allowlist. Null or empty means no slides.</summary>
    public Guid[]? HeroLibraryIds { get; set; }

    /// <summary>Serve the Discovery view on new installs. Seerr and cross-user modules remain independent opt-ins.</summary>
    public bool DiscoveryPageEnabled { get; set; } = true;

    /// <summary>Serves the isolated global search adapter; disabled by default and does not inject it.</summary>
    public bool GlobalSearchEnabled { get; set; }

    /// <summary>Gets or sets the optional Recently Added library allowlist. Null means all eligible libraries.</summary>
    public Guid[]? RecentlyAddedLibraryIds { get; set; }

    /// <summary>Gets or sets whether server-side Seerr integration is enabled. Disabled by default.</summary>
    public bool SeerrEnabled { get; set; }

    /// <summary>Household-wide requests for existing signed-in users. Default off.</summary>
    public bool SharedRequestsEnabled { get; set; }

    /// <summary>Gets or sets the Seerr server base URL. Never include this in a user-facing DTO.</summary>
    public string? SeerrBaseUrl { get; set; }

    /// <summary>Gets or sets the server-side Seerr API key. Never log or return this to users.</summary>
    public string? SeerrApiKey { get; set; }

    /// <summary>Administrator opt-in for household-wide title activity, including titles outside a user's libraries. Disabled by default.</summary>
    public bool DownloadsEnabled { get; set; }

    /// <summary>Administrator opt-in for household-wide upcoming Arr titles, including titles outside a user's libraries. Independent of Downloads.</summary>
    public bool CalendarEnabled { get; set; }
    /// <summary>Household Arr upcoming movie cards; requires Home and Calendar. Default off.</summary>
    public bool UpcomingMoviesRowEnabled { get; set; }
    /// <summary>Household Arr upcoming episode cards; requires Home and Calendar. Default off.</summary>
    public bool UpcomingShowsRowEnabled { get; set; }

    /// <summary>Server-only Radarr connection; never sent to browsers.</summary>
    public string? RadarrBaseUrl { get; set; }
    public string? RadarrApiKey { get; set; }

    /// <summary>Server-only Sonarr connection; never sent to browsers.</summary>
    public string? SonarrBaseUrl { get; set; }
    public string? SonarrApiKey { get; set; }
}
