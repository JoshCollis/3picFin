using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rowan.Jellyfin.Plugin.Downloads;

public sealed record DownloadTitle(string Source, string MediaType, int TitleId, string Title, string State, double? Progress);
public sealed record DownloadSource(IReadOnlyList<DownloadTitle> Items, bool Partial, string? Error);
public sealed record DownloadsResponse(DownloadSource Radarr, DownloadSource Sonarr);

/// <summary>Read-only, household-wide title-level Arr queue. Does not attribute work to requests or library users.</summary>
public sealed class ArrDownloadsClient(HttpClient http, ArrDownloadsOptions? options, DownloadsAdmission admission)
{
    private const int PageSize = 50, MaxPages = 3, MaxItems = 100, MaxResponseBytes = 128 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    private static readonly DownloadSource Disabled = new([], false, "Disabled");
    private static readonly DownloadSource Unavailable = new([], false, "UpstreamUnavailable");
    private static readonly DownloadSource InvalidConfiguration = new([], true, "InvalidConfiguration");

    /// <summary>Null indicates admission overflow; external errors are only typed, never upstream text.</summary>
    public async Task<DownloadsResponse?> GetAsync(Guid user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options is null) return new(Disabled, Disabled);
        using var lease = admission.TryEnter(user);
        if (lease is null) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        var radarr = options.RadarrInvalid ? InvalidConfiguration : await ReadAsync(options.Radarr, "movie", deadline.Token, cancellationToken).ConfigureAwait(false);
        var sonarr = options.SonarrInvalid ? InvalidConfiguration : await ReadAsync(options.Sonarr, "series", deadline.Token, cancellationToken).ConfigureAwait(false);
        return new(radarr, sonarr);
    }

    private async Task<DownloadSource> ReadAsync(ArrEndpoint? endpoint, string kind, CancellationToken token, CancellationToken caller)
    {
        if (endpoint is null) return Disabled;
        var items = new List<DownloadTitle>();
        var identities = new Dictionary<int, (int LocalId, string Name, int Index, int Count, double Sum, bool Valid)>();
        var localIdentities = new Dictionary<int, int>();
        var recordCount = 0;
        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                token.ThrowIfCancellationRequested();
                var path = $"api/v3/queue?page={page}&pageSize={PageSize}&include{(kind == "movie" ? "Movie" : "Series")}=true";
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.BaseUri, path));
                request.Headers.TryAddWithoutValidation("X-Api-Key", endpoint.ApiKey);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                // Redirects are errors, never followed with credentials (production handler disables auto-redirect).
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxResponseBytes) return Unavailable;
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > MaxResponseBytes) return Unavailable;
                    buffer.Write(chunk, 0, count);
                }
                using var json = JsonDocument.Parse(buffer.ToArray());
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || Number(root, "page") != page || Number(root, "pageSize") != PageSize ||
                    Number(root, "totalRecords") is not { } total || total < 0 || total > 1_000_000 ||
                    !root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > PageSize ||
                    records.GetArrayLength() == 0 && recordCount < total || recordCount + records.GetArrayLength() > total) return Unavailable;
                foreach (var record in records.EnumerateArray())
                {
                    if (!TryTitle(record, kind, out var title)) return Unavailable;
                    var localId = Number(record, kind == "movie" ? "movieId" : "seriesId")!.Value;
                    var name = record.GetProperty(kind).GetProperty("title").GetString()!.Trim();
                    if (localIdentities.TryGetValue(localId, out var priorExternalId) && priorExternalId != title.TitleId) return Unavailable;
                    localIdentities[localId] = title.TitleId;
                    if (identities.TryGetValue(title.TitleId, out var existing))
                    {
                        if (existing.LocalId != localId || !StringComparer.Ordinal.Equals(existing.Name, name)) return Unavailable;
                        var entryCount = existing.Count + 1;
                        var valid = existing.Valid && title.Progress.HasValue;
                        identities[title.TitleId] = (localId, name, existing.Index, entryCount, existing.Sum + (title.Progress ?? 0), valid);
                        var previous = items[existing.Index];
                        items[existing.Index] = previous with
                        {
                            State = StateRank(title.State) > StateRank(previous.State) ? title.State : previous.State,
                            Progress = valid ? Math.Round((existing.Sum + title.Progress!.Value) / entryCount, 4) : null
                        };
                    }
                    else
                    {
                        identities.Add(title.TitleId, (localId, name, items.Count, 1, title.Progress ?? 0, title.Progress.HasValue));
                        items.Add(title);
                    }
                }
                recordCount += records.GetArrayLength();
                if (recordCount >= total) return new(items, false, null);
                if (page == MaxPages || recordCount >= MaxItems) return new(items, true, null);
                if (records.GetArrayLength() != PageSize) return Unavailable;
            }
            return Unavailable;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { return Unavailable; }
        catch (HttpRequestException) { return Unavailable; }
        catch (IOException) { return Unavailable; }
        catch (JsonException) { return Unavailable; }
        catch (InvalidOperationException) { return Unavailable; }
    }

    // Active work wins over queued or completed records; ties are order-independent.
    private static int StateRank(string state) => state switch
    {
        "Downloading" => 5, "Paused" => 4, "Queued" => 3, "Unknown" => 2, _ => 1
    };

    private static bool TryTitle(JsonElement record, string kind, out DownloadTitle title)
    {
        title = null!;
        if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty(kind, out var nested) || nested.ValueKind != JsonValueKind.Object) return false;
        var localId = Number(record, kind == "movie" ? "movieId" : "seriesId");
        var nestedId = Number(nested, "id");
        var externalId = Number(nested, kind == "movie" ? "tmdbId" : "tvdbId");
        if (localId is not > 0 || nestedId != localId || externalId is not > 0 or > 100_000_000 ||
            !nested.TryGetProperty("title", out var name) || name.ValueKind != JsonValueKind.String ||
            name.GetString() is not { } text || string.IsNullOrWhiteSpace(text)) return false;
        var state = Text(record, "status") switch
        {
            "downloading" => "Downloading", "queued" => "Queued", "paused" => "Paused", "completed" => "Completed", _ => "Unknown"
        };
        double? progress = null;
        if (Number64(record, "size") is { } size && size > 0 && Number64(record, "sizeleft") is { } left && left >= 0 && left <= size)
            progress = Math.Round((double)(size - left) / size, 4);
        title = new(kind == "movie" ? "Radarr" : "Sonarr", kind == "movie" ? "movie" : "tv", externalId.Value,
            text.Trim()[..Math.Min(200, text.Trim().Length)], state, progress);
        return true;
    }

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static int? Number(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) ? number : null;
    private static long? Number64(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var number) ? number : null;
}
