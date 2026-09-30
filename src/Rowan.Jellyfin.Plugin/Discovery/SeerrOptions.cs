using System;
using System.Text.Json.Serialization;
using Rowan.Jellyfin.Plugin.Configuration;

namespace Rowan.Jellyfin.Plugin.Discovery;

/// <summary>Validated server-only Seerr connection settings; null means no upstream calls.</summary>
public sealed class SeerrOptions
{
    private SeerrOptions(Uri baseUri, string apiKey, bool enable4kRequests)
    {
        BaseUri = baseUri;
        ApiKey = apiKey;
        Enable4kRequests = enable4kRequests;
    }

    public Uri BaseUri { get; }
    public bool Enable4kRequests { get; }

    /// <summary>Secret for server-side transport only. Never return in a user-facing response or log.</summary>
    [JsonIgnore]
    public string ApiKey { get; }

    /// <summary>Resolve effective settings. Disabled or incomplete configuration never permits an upstream call.</summary>
    public static SeerrOptions? FromConfiguration(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.SeerrEnabled || string.IsNullOrWhiteSpace(configuration.SeerrBaseUrl) || string.IsNullOrWhiteSpace(configuration.SeerrApiKey))
        {
            return null;
        }

        var rawUrl = configuration.SeerrBaseUrl.Trim();
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host)
            || rawUrl.IndexOf("://", StringComparison.Ordinal) < 0
            || rawUrl.Contains('#')
            || rawUrl.Contains('?')
            || rawUrl[(rawUrl.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0].Contains('@'))
        {
            throw new ArgumentException("Seerr base URL must be an absolute HTTP(S) URL without userinfo, query, or fragment.", nameof(configuration));
        }

        var baseUri = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + "/");
        return new SeerrOptions(baseUri, configuration.SeerrApiKey, configuration.Enable4kRequests);
    }

    public override string ToString() => "SeerrOptions { credentials = [REDACTED] }";
}
