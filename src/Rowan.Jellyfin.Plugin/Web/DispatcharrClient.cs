using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Rowan.Jellyfin.Plugin.Web;

/// <summary>Operator-selected management origin. Never derived from a playback address.</summary>
public sealed class DispatcharrConnection
{
    public Uri BaseUri { get; }
    [JsonIgnore] internal string Key { get; }
    private DispatcharrConnection(Uri uri, string key) { BaseUri = uri; Key = key; }
    public static DispatcharrConnection Parse(string url, string key)
    {
        if (url.Length > 2048 || key.Length is < 1 or > 512 || key.Any(c => c < 33 || c > 126) ||
            url.Any(char.IsWhiteSpace) || url.IndexOfAny(['%', '\\', '?', '#', '@']) >= 0 ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !Uri.IsWellFormedUriString(url, UriKind.Absolute) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            url.Split('/').Any(p => p is "." or "..")) throw new InvalidDataException("Invalid metadata connection settings.");
        return new(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"), key);
    }
    public override string ToString() => "DispatcharrConnection { [REDACTED] }";
}

public sealed record DispatcharrMetadata(IReadOnlyDictionary<Guid, long?> Channels, IReadOnlyDictionary<long, string> Groups);
public sealed record DispatcharrCacheChannel(string ExternalId, Guid Uuid);

public static class DispatcharrIdentity
{
    // The address is identifier data ONLY: no DNS, HTTP, provider or playback probe.
    private static readonly Regex PathPattern = new(@"\Ahttps?://[^/?#@\\%\s]+/proxy/ts/stream/([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    public static bool TryRead(string? path, out Guid id)
    {
        id = Guid.Empty;
        if (path is null || path.Length > 4096 || !Uri.TryCreate(path, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return false;
        var match = PathPattern.Match(path);
        return match.Success && Guid.TryParseExact(match.Groups[1].Value, "D", out id) && id != Guid.Empty;
    }

    public static GuideAssignedGroup[] Assign(DispatcharrCacheChannel[] cache, DispatcharrMetadata metadata)
    {
        if (cache.Length > GuideCategoryPolicy.MaximumChannels || cache.Any(c => string.IsNullOrEmpty(c.ExternalId) || c.Uuid == Guid.Empty) ||
            cache.Select(c => c.ExternalId).Distinct(StringComparer.Ordinal).Count() != cache.Length || cache.Select(c => c.Uuid).Distinct().Count() != cache.Length)
            throw new InvalidDataException();
        return cache.Select(c => {
            if (!metadata.Channels.TryGetValue(c.Uuid, out var group)) throw new InvalidDataException();
            if (group is null) return new GuideAssignedGroup(c.ExternalId, "");
            if (!metadata.Groups.TryGetValue(group.Value, out var name)) throw new InvalidDataException();
            return new GuideAssignedGroup(c.ExternalId, name, "dispatcharr\0group:" + group.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }).ToArray();
    }
}

/// <summary>Two fixed read-only metadata GETs; one snapshot, one active read, no next URL or stream requests.</summary>
public sealed class DispatcharrClient(HttpClient http, TimeSpan? timeout = null)
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DispatcharrMetadata? _cached;
    private string? _revision;
    private DateTimeOffset _expires;

    public async Task<DispatcharrMetadata> Read(DispatcharrSettings settings, CancellationToken token)
    {
        if (!settings.Enabled || settings.Connection is null) throw new InvalidDataException("Category source is not configured.");
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidDataException("Category source is busy. Retry shortly.");
        try
        {
            if (_revision == settings.Revision && DateTimeOffset.UtcNow < _expires)
                return _cached ?? throw new InvalidDataException("Category source is unavailable. Retry shortly.");
            _cached = null; _revision = settings.Revision; _expires = DateTimeOffset.MinValue;
            try
            {
                _cached = await Fetch(settings.Connection, token).ConfigureAwait(false);
                _expires = DateTimeOffset.UtcNow.AddSeconds(30);
                return _cached;
            }
            catch (InvalidDataException) { _expires = DateTimeOffset.UtcNow.AddSeconds(5); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task<DispatcharrMetadata> Fetch(DispatcharrConnection connection, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        var remaining = MaximumBytes;
        async Task<JsonDocument> Get(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.BaseUri, path));
            request.Headers.Add("X-API-Key", connection.Key);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentLength > remaining ||
                response.Content.Headers.ContentType?.MediaType != "application/json") throw new InvalidDataException();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(block.AsMemory(0, Math.Min(block.Length, remaining + 1)), deadline.Token).ConfigureAwait(false)) > 0)
            {
                remaining -= count;
                if (remaining < 0) throw new InvalidDataException();
                buffer.Write(block, 0, count);
            }
            var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            try { UniqueProperties(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
        try
        {
            // Verified installed Dispatcharr action: effective group overrides, no stream fields.
            // Both endpoints return arrays. Paginated/next-link shapes are unsupported, never followed.
            using var channels = await Get("api/channels/channels/summary/").ConfigureAwait(false);
            if (channels.RootElement.ValueKind != JsonValueKind.Array || channels.RootElement.GetArrayLength() > 10000) throw new InvalidDataException();
            var members = new Dictionary<Guid, long?>();
            foreach (var row in channels.RootElement.EnumerateArray())
            {
                var raw = row.GetProperty("uuid").GetString();
                if (!Guid.TryParseExact(raw, "D", out var id) || id == Guid.Empty || id.ToString("D") != raw) throw new InvalidDataException();
                var group = row.GetProperty("channel_group_id");
                long? groupId = group.ValueKind == JsonValueKind.Null ? null : group.GetInt64();
                if (groupId <= 0 || !members.TryAdd(id, groupId)) throw new InvalidDataException();
            }
            using var groups = await Get("api/channels/groups/").ConfigureAwait(false);
            if (groups.RootElement.ValueKind != JsonValueKind.Array || groups.RootElement.GetArrayLength() > 10000) throw new InvalidDataException();
            var names = new Dictionary<long, string>();
            foreach (var row in groups.RootElement.EnumerateArray())
            {
                var id = row.GetProperty("id").GetInt64(); var name = row.GetProperty("name").GetString();
                if (id <= 0 || string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl) ||
                    name.Contains("://", StringComparison.Ordinal) || !names.TryAdd(id, name)) throw new InvalidDataException();
            }
            if (members.Values.Any(id => id.HasValue && !names.ContainsKey(id.Value))) throw new InvalidDataException();
            return new(members, names);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidDataException("Category source timed out. Retry shortly."); }
        catch (Exception e) when (e is InvalidDataException or HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new InvalidDataException("Category source metadata is unavailable or invalid. Retry shortly."); }
    }

    internal static void UniqueProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException(); UniqueProperties(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) UniqueProperties(child);
    }
}
