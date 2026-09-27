using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rowan.Jellyfin.Plugin.Downloads;
using Rowan.Jellyfin.Plugin.Configuration;

namespace Rowan.Jellyfin.Plugin.Calendar;

public sealed record CalendarEvent(string Source, string MediaType, int TitleId, string Title, string EventType, DateTimeOffset Date, int? SeasonNumber, int? EpisodeNumber, string? EpisodeTitle, [property: System.Text.Json.Serialization.JsonIgnore] bool? Monitored = null, [property: System.Text.Json.Serialization.JsonIgnore] bool? HasFile = null);
public sealed record CalendarSource(IReadOnlyList<CalendarEvent> Items, bool Partial, string? Error);
public sealed record CalendarResponse(CalendarSource Radarr, CalendarSource Sonarr);

public sealed class ArrCalendarOptions
{
    private ArrCalendarOptions(ArrDownloadsOptions? endpoints) { Endpoints = endpoints; }
    internal ArrDownloadsOptions? Endpoints { get; }
    public static ArrCalendarOptions? TryFromConfiguration(PluginConfiguration configuration) =>
        configuration.CalendarEnabled ? new(ArrDownloadsOptions.TryForCalendar(configuration)) : null;
}

/// <summary>Independent, non-queuing calendar admission, separate from Downloads.</summary>
public sealed class CalendarAdmission(int globalLimit = 4, int perUserLimit = 1)
{
    private readonly DownloadsAdmission _admission = new(globalLimit, perUserLimit);
    public IDisposable? TryEnter(Guid user) => _admission.TryEnter(user);
}

/// <summary>Read-only shared Arr calendar. No media availability or per-user watchability assertions.</summary>
public sealed class ArrCalendarClient(HttpClient http, ArrCalendarOptions? options, CalendarAdmission admission)
{
    private const int MaxRecords = 100, MaxResponseBytes = 128 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    private static readonly CalendarSource Disabled = new([], false, "Disabled");
    private static readonly CalendarSource Invalid = new([], true, "InvalidConfiguration");
    private static readonly CalendarSource Unavailable = new([], true, "UpstreamUnavailable");

    public async Task<CalendarResponse?> GetAsync(Guid user, DateTime start, DateTime end, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        if (options is null) return new(Disabled, Disabled);
        using var lease = admission.TryEnter(user);
        if (lease is null) return null;
        var endpoints = options.Endpoints;
        var radarrTask = endpoints?.RadarrInvalid == true ? Task.FromResult(Invalid) : ReadWithDeadlineAsync(endpoints?.Radarr, false, start, end, caller);
        var sonarrTask = endpoints?.SonarrInvalid == true ? Task.FromResult(Invalid) : ReadWithDeadlineAsync(endpoints?.Sonarr, true, start, end, caller);
        await Task.WhenAll(radarrTask, sonarrTask).ConfigureAwait(false);
        var radarr = await radarrTask.ConfigureAwait(false);
        var sonarr = await sonarrTask.ConfigureAwait(false);
        return new(radarr, sonarr);
    }

    private async Task<CalendarSource> ReadWithDeadlineAsync(ArrEndpoint? endpoint, bool sonarr, DateTime start, DateTime end, CancellationToken caller)
    {
        if (endpoint is null) return Disabled;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(Deadline);
        return await ReadAsync(endpoint, sonarr, start, end, deadline.Token, caller).ConfigureAwait(false);
    }

    private async Task<CalendarSource> ReadAsync(ArrEndpoint? endpoint, bool sonarr, DateTime start, DateTime end, CancellationToken token, CancellationToken caller)
    {
        if (endpoint is null) return Disabled;
        try
        {
            token.ThrowIfCancellationRequested();
            var query = $"api/v3/calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}&unmonitored=false" +
                (sonarr ? "&includeSeries=true&includeEpisodeFile=false&includeEpisodeImages=false" : "");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.BaseUri, query));
            request.Headers.TryAddWithoutValidation("X-Api-Key", endpoint.ApiKey);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            // The registered handler never follows redirects; do not send keys to another origin.
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxResponseBytes) return Unavailable;
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes) return Unavailable;
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return Unavailable;
            var partial = root.GetArrayLength() > MaxRecords;
            var events = new List<CalendarEvent>();
            var seen = new HashSet<(int, string, long)>();
            var identities = new Dictionary<int, (int ExternalId, string Title)>();
            var externalIds = new Dictionary<int, int>();
            var statuses = new Dictionary<int, (bool? Monitored, bool? HasFile)>();
            var from = new DateTimeOffset(start, TimeSpan.Zero);
            var until = new DateTimeOffset(end, TimeSpan.Zero);
            var processed = 0;
            foreach (var record in root.EnumerateArray())
            {
                if (processed++ == MaxRecords) break;
                if (record.ValueKind != JsonValueKind.Object) return Unavailable;
                if (!TryOptionalBoolean(record, "monitored", out var monitored)) return Unavailable;
                bool? hasFile;
                if (sonarr)
                {
                    if (!TryOptionalBoolean(record, "hasFile", out hasFile)) return Unavailable;
                    if (!TryNumber(record, "id", out var episodeId) || !TryNumber(record, "seriesId", out var seriesId) ||
                        !TryNumber(record, "seasonNumber", out var season, allowZero: true) || !TryNumber(record, "episodeNumber", out var episode, allowZero: true) ||
                        !record.TryGetProperty("series", out var series) || series.ValueKind != JsonValueKind.Object ||
                        !TryNumber(series, "id", out var nestedId) || nestedId != seriesId ||
                        !TryNumber(series, "tvdbId", out var externalId) || !TryTitle(series, out var seriesTitle) ||
                        !TryTitle(record, out var episodeTitle) || !TryDate(record, "airDateUtc", out var airDate)) return Unavailable;
                    if (!Consistent(seriesId, externalId, seriesTitle, identities, externalIds)) return Unavailable;
                    if (!ConsistentStatus(episodeId, monitored, hasFile, statuses)) return Unavailable;
                    if (airDate >= from && airDate < until && seen.Add((episodeId, "Episode", airDate.UtcTicks)))
                        events.Add(new("Sonarr", "tv", externalId, seriesTitle, "Episode", airDate, season, episode, episodeTitle, monitored, hasFile));
                }
                else
                {
                    // Radarr 6.4.4 maps MovieFileId, but leaves compatibility HasFile null.
                    // Missing ID is unknown; malformed/null IDs invalidate this source.
                    hasFile = null;
                    if (record.TryGetProperty("movieFileId", out var fileId))
                    {
                        if (fileId.ValueKind != JsonValueKind.Number || !fileId.TryGetInt32(out var value) || value < 0 || value > 100_000_000) return Unavailable;
                        hasFile = value > 0;
                    }
                    if (!TryNumber(record, "id", out var id) || !TryNumber(record, "tmdbId", out var externalId) || !TryTitle(record, out var title)) return Unavailable;
                    if (!Consistent(id, externalId, title, identities, externalIds)) return Unavailable;
                    if (!ConsistentStatus(id, monitored, hasFile, statuses)) return Unavailable;
                    foreach (var (field, type) in new[] { ("inCinemas", "Cinema"), ("digitalRelease", "Digital"), ("physicalRelease", "Physical") })
                    {
                        if (!TryOptionalDate(record, field, out var date)) return Unavailable;
                        if (date is { } value && value >= from && value < until && seen.Add((id, type, value.UtcTicks)))
                            events.Add(new("Radarr", "movie", externalId, title, type, value, null, null, null, monitored, hasFile));
                    }
                }
            }
            return new(events, partial, null);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { return Unavailable; }
        catch (HttpRequestException) { return Unavailable; }
        catch (IOException) { return Unavailable; }
        catch (JsonException) { return Unavailable; }
        catch (InvalidOperationException) { return Unavailable; }
    }

    private static bool ConsistentStatus(int id, bool? monitored, bool? hasFile, Dictionary<int, (bool? Monitored, bool? HasFile)> statuses)
    {
        if (statuses.TryGetValue(id, out var previous) && (previous.Monitored != monitored || previous.HasFile != hasFile)) return false;
        statuses[id] = (monitored, hasFile);
        return true;
    }

    private static bool Consistent(int local, int external, string title, Dictionary<int, (int ExternalId, string Title)> identities, Dictionary<int, int> externalIds)
    {
        if (identities.TryGetValue(local, out var previous) && (previous.ExternalId != external || !StringComparer.Ordinal.Equals(previous.Title, title))) return false;
        if (externalIds.TryGetValue(external, out var previousLocal) && previousLocal != local) return false;
        identities[local] = (external, title);
        externalIds[external] = local;
        return true;
    }

    private static bool TryNumber(JsonElement value, string key, out int number, bool allowZero = false)
    {
        number = 0;
        return value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out number) && number >= (allowZero ? 0 : 1) && number <= 100_000_000;
    }
    private static bool TryOptionalBoolean(JsonElement value, string key, out bool? result)
    {
        result = null;
        if (!value.TryGetProperty(key, out var field)) return true;
        if (field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result = field.GetBoolean();
        return true;
    }
    private static bool TryTitle(JsonElement value, out string title)
    {
        title = "";
        if (!value.TryGetProperty("title", out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString())) return false;
        title = field.GetString()!.Trim();
        title = title[..Math.Min(200, title.Length)];
        return true;
    }
    private static bool TryOptionalDate(JsonElement value, string key, out DateTimeOffset? date)
    {
        date = null;
        if (!value.TryGetProperty(key, out var field) || field.ValueKind == JsonValueKind.Null) return true;
        if (field.ValueKind != JsonValueKind.String) return false;
        if (!TryParseUtc(field.GetString(), out var parsed)) return false;
        date = parsed;
        return true;
    }
    private static bool TryDate(JsonElement value, string key, out DateTimeOffset date)
    {
        date = default;
        return value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String && TryParseUtc(field.GetString(), out date);
    }
    private static bool TryParseUtc(string? text, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date) &&
        text is not null && (text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal)) && date.Year is >= 1900 and <= 2200;
}
