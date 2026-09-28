using System;
using Microsoft.AspNetCore.Mvc;

namespace Rowan.Jellyfin.Plugin.Web;

/// <summary>Serves only public, embedded, user-independent page resources; no navigation is registered here.</summary>
[ApiController]
[Route("3picFin/Web/{asset}")]
public sealed class DiscoveryPageController : ControllerBase
{
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _searchEnabled;
    private readonly Func<bool> _homeEnabled;
    private readonly Func<bool> _heroEnabled;
    private readonly Func<bool> _rowsEnabled;

    /// <summary>Jellyfin activation path; no external service or network access.</summary>
    public DiscoveryPageController() : this(() => Plugin.Current?.Configuration.DiscoveryPageEnabled == true,
        () => Plugin.Current?.Configuration.GlobalSearchEnabled == true,
        () => Plugin.Current?.Configuration.HomeEnabled == true,
        () => HomeAdapterRegistration.HeroConfigured(Plugin.Current?.Configuration),
        () => Plugin.Current?.Configuration is { HomeEnabled: true, NativeHomeRowsEnabled: true, NativeHomeRowKinds.Length: > 0 }) { }

    /// <summary>Enables isolated route tests without a live Jellyfin plugin instance.</summary>
    public DiscoveryPageController(Func<bool> enabled) : this(enabled, () => false, () => false, () => false) { }

    /// <summary>Enables independent search-asset gate tests.</summary>
    public DiscoveryPageController(Func<bool> enabled, Func<bool> searchEnabled) : this(enabled, searchEnabled, () => false, () => false) { }

    /// <summary>Enables independent Home asset gate tests.</summary>
    public DiscoveryPageController(Func<bool> enabled, Func<bool> searchEnabled, Func<bool> homeEnabled)
        : this(enabled, searchEnabled, homeEnabled, () => false) { }

    public DiscoveryPageController(Func<bool> enabled, Func<bool> searchEnabled, Func<bool> homeEnabled, Func<bool> heroEnabled)
        : this(enabled, searchEnabled, homeEnabled, heroEnabled, () => false) { }

    public DiscoveryPageController(Func<bool> enabled, Func<bool> searchEnabled, Func<bool> homeEnabled, Func<bool> heroEnabled, Func<bool> rowsEnabled)
    {
        _enabled = enabled;
        _searchEnabled = searchEnabled;
        _homeEnabled = homeEnabled;
        _heroEnabled = heroEnabled;
        _rowsEnabled = rowsEnabled;
    }

    /// <summary>Returns only allowlisted resources, gated by independent disabled-by-default UI flags.</summary>
    [HttpGet]
    public IActionResult GetAsset(string asset)
    {
        var searchAsset = asset is "global-search-addon.js" or "global-search-addon.css" or "search-adapter.js";
        var homeAsset = asset is "home-adapter.js" or "home-tab-host.js" or "home-tab-host.css";
        var navAsset = asset is "global-fin-nav.js" or "global-fin-nav.css";
        var heroAsset = asset is "static-hero.js" or "static-hero.css";
        if (asset is "native-home-rows.js" or "native-home-rows.css" ? !_homeEnabled() :
            searchAsset ? !_searchEnabled() : heroAsset ? !(_homeEnabled() && _heroEnabled()) :
            navAsset ? !(_enabled() && _homeEnabled()) :
            homeAsset ? !(_enabled() || (_homeEnabled() && (_heroEnabled() || _rowsEnabled()))) :
            asset is "discovery.html" or "discovery.js" or "discovery.css" ? !(_enabled() || _searchEnabled()) : !_enabled()) return NotFound();
        var contentType = asset switch
        {
            "global-search-addon.js" => "text/javascript; charset=utf-8",
            "search-adapter.js" => "text/javascript; charset=utf-8",
            "global-search-addon.css" => "text/css; charset=utf-8",
            "discovery.html" => "text/html; charset=utf-8",
            "discovery.css" => "text/css; charset=utf-8",
            "discovery.js" => "text/javascript; charset=utf-8",
            "home-tab-host.js" => "text/javascript; charset=utf-8",
            "home-tab-host.css" => "text/css; charset=utf-8",
            "static-hero.js" => "text/javascript; charset=utf-8",
            "static-hero.css" => "text/css; charset=utf-8",
            "home-adapter.js" => "text/javascript; charset=utf-8",
            "global-fin-nav.js" => "text/javascript; charset=utf-8",
            "global-fin-nav.css" => "text/css; charset=utf-8",
            "native-home-rows.js" => "text/javascript; charset=utf-8",
            "native-home-rows.css" => "text/css; charset=utf-8",
            _ => null
        };
        if (contentType is null) return NotFound();
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"Rowan.Jellyfin.Plugin.Web.{asset}");
        return stream is null ? NotFound() : File(stream, contentType);
    }
}
