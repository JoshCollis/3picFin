using System;
using Rowan.Jellyfin.Plugin.Configuration;

namespace Rowan.Jellyfin.Plugin.Downloads;

/// <summary>Validated server-side settings. No setting or secret is serialized into a response.</summary>
public sealed class ArrDownloadsOptions
{
    private const int MaxUrlLength = 2048, MaxKeyLength = 256;
    private ArrDownloadsOptions(ArrEndpoint? radarr, ArrEndpoint? sonarr, bool radarrInvalid = false, bool sonarrInvalid = false)
    { Radarr = radarr; Sonarr = sonarr; RadarrInvalid = radarrInvalid; SonarrInvalid = sonarrInvalid; }
    public ArrEndpoint? Radarr { get; }
    public ArrEndpoint? Sonarr { get; }
    public bool RadarrInvalid { get; }
    public bool SonarrInvalid { get; }

    public static ArrDownloadsOptions? FromConfiguration(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.DownloadsEnabled) return null;
        var radarr = Parse(configuration.RadarrBaseUrl, configuration.RadarrApiKey);
        var sonarr = Parse(configuration.SonarrBaseUrl, configuration.SonarrApiKey);
        return radarr is null && sonarr is null ? null : new(radarr, sonarr);
    }

    public static ArrDownloadsOptions? TryFromConfiguration(PluginConfiguration configuration) => TryForModule(configuration, configuration.DownloadsEnabled);

    /// <summary>Reuse endpoint validation for Calendar without enabling Downloads.</summary>
    public static ArrDownloadsOptions? TryForCalendar(PluginConfiguration configuration) => TryForModule(configuration, configuration.CalendarEnabled);

    private static ArrDownloadsOptions? TryForModule(PluginConfiguration configuration, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!enabled) return null;
        ArrEndpoint? radarr = null, sonarr = null;
        var radarrInvalid = false;
        var sonarrInvalid = false;
        try { radarr = Parse(configuration.RadarrBaseUrl, configuration.RadarrApiKey); }
        catch (ArgumentException) { radarrInvalid = true; }
        try { sonarr = Parse(configuration.SonarrBaseUrl, configuration.SonarrApiKey); }
        catch (ArgumentException) { sonarrInvalid = true; }
        return radarr is null && sonarr is null && !radarrInvalid && !sonarrInvalid
            ? null : new(radarr, sonarr, radarrInvalid, sonarrInvalid);
    }

    private static ArrEndpoint? Parse(string? url, string? key)
    {
        if (url?.Length > MaxUrlLength || key?.Length > MaxKeyLength)
            throw new ArgumentException("Arr endpoint configuration exceeds length limit.");
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(key)) return null;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key) || key.Contains('\r') || key.Contains('\n'))
            throw new ArgumentException("Incomplete Arr endpoint configuration.");
        var raw = url.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || raw.Contains('@') || raw.Contains('?') || raw.Contains('#') ||
            raw.Contains('\\') || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Arr endpoint must be an absolute HTTP(S) URL without credentials, query or fragment.");
        return new(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"), key);
    }
}

public sealed class ArrEndpoint(Uri baseUri, string apiKey)
{
    public Uri BaseUri { get; } = baseUri;
    [System.Text.Json.Serialization.JsonIgnore]
    public string ApiKey { get; } = apiKey;
    public override string ToString() => "ArrEndpoint [REDACTED]";
}
