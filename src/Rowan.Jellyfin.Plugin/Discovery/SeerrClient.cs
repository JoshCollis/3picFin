using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rowan.Jellyfin.Plugin.Discovery;

public enum SeerrFailure
{
    Disabled,
    UserNotMapped,
    InvalidMapping,
    UpstreamUnavailable,
    PermissionDenied,
    AlreadyRequested,
    InvalidSeason,
    CsrfUnsupported
}

public sealed record CreateSeerrRequest(string MediaType, int MediaId, int[]? Seasons, bool Is4k);
public sealed record CreatedSeerrRequest(int Id, int Status);
public sealed record SeerrCreateResult(CreatedSeerrRequest? Value, SeerrFailure? Failure);
public sealed record RequestOptions(bool CanRequest, bool CanRequest4k, int? MediaStatus, int? MediaStatus4k, int[] Seasons);
public sealed record RequestOptionsResult(RequestOptions? Value, SeerrFailure? Failure);

/// <summary>Only sanitized data or a typed failure; never an upstream exception or response body on failure.</summary>
public sealed class SeerrResult
{
    private SeerrResult(JsonElement? value, SeerrFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public JsonElement? Value { get; }
    public SeerrFailure? Failure { get; }
    public bool IsSuccess => Failure is null;
    public static SeerrResult Success(JsonElement value) => new(value, null);
    public static SeerrResult Fail(SeerrFailure failure) => new(null, failure);
    public override string ToString() => Failure?.ToString() ?? "Success";
}

/// <summary>Independent source outcomes after one identity mapping.</summary>
public sealed record SeerrBundle(SeerrResult Movies, SeerrResult Tv, SeerrResult Requests);

/// <summary>Read-only, per-call Jellyfin-to-Seerr identity binding. No identity cache or browser-supplied Seerr ID.</summary>
public sealed class SeerrClient
{
    private const int MaxResponseBytes = 1_048_576;
    private static readonly SemaphoreSlim SharedReadSlots = new(4, 4);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _http;
    private readonly SeerrOptions? _options;
    private readonly TimeSpan _timeout;
    private readonly SeerrReadCache _readCache;
    private readonly string _cacheScope;

    public SeerrClient(HttpClient http, SeerrOptions? options, TimeSpan? timeout = null, SeerrReadCache? readCache = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options;
        _timeout = timeout is { } requested && requested > TimeSpan.Zero && requested < RequestTimeout ? requested : RequestTimeout;
        _readCache = readCache ?? new SeerrReadCache();
        // Isolate config changes without retaining raw API credentials in cache keys.
        _cacheScope = options is null ? string.Empty :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.BaseUri + ":" + options.ApiKey)));
    }

    /// <summary>One mapped detail read under the same deadline; never returns raw upstream JSON to the route.</summary>
    public async Task<TitleDetailResult> GetTitleDetailAsync(Guid userId, string type, int mediaId, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        if (_options is null) return new(null, null, SeerrFailure.Disabled);
        if (userId == Guid.Empty || type is not ("movie" or "tv") || mediaId is < 1 or > 100_000_000)
            return new(null, null, SeerrFailure.UserNotMapped);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(_timeout);
        try
        {
            var mapping = await GetAsync($"api/v1/user/jellyfin/{userId:D}", null, deadline.Token).ConfigureAwait(false);
            if (mapping.Status == HttpStatusCode.NotFound) return new(null, null, SeerrFailure.UserNotMapped);
            if (mapping.Status != HttpStatusCode.OK || mapping.Body is null) return new(null, null, SeerrFailure.UpstreamUnavailable);
            using var identity = JsonDocument.Parse(mapping.Body);
            var user = identity.RootElement;
            if (!user.TryGetProperty("id", out var id) || !id.TryGetInt32(out var mappedId) || mappedId <= 0 ||
                !user.TryGetProperty("permissions", out var bits) || !bits.TryGetInt32(out var permissions) || permissions < 0)
                return new(null, null, SeerrFailure.InvalidMapping);
            var detail = await GetAsync($"api/v1/{type}/{mediaId.ToString(CultureInfo.InvariantCulture)}", mappedId, deadline.Token).ConfigureAwait(false);
            if (detail.Status != HttpStatusCode.OK || detail.Body is null) return new(null, null, SeerrFailure.UpstreamUnavailable);
            using var document = JsonDocument.Parse(detail.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var actual) ||
                !actual.TryGetInt32(out var actualId) || actualId != mediaId)
                return new(null, null, SeerrFailure.UpstreamUnavailable);
            string? Text(JsonElement value, string key, int max) => value.TryGetProperty(key, out var field) &&
                field.ValueKind == JsonValueKind.String && field.GetString() is { } text ? text[..Math.Min(text.Length, max)] : null;
            var title = Text(root, type == "tv" ? "name" : "title", 200);
            if (string.IsNullOrWhiteSpace(title)) return new(null, null, SeerrFailure.UpstreamUnavailable);
            var date = Text(root, type == "tv" ? "firstAirDate" : "releaseDate", 11);
            if (date is null || date.Length != 10 || !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)) date = null;
            var poster = Text(root, "posterPath", 200);
            if (poster is not null && (!poster.StartsWith('/') || poster.StartsWith("//") || poster.Contains("..") || poster.Contains('\\') ||
                poster.IndexOfAny(['?', '#', '\r', '\n']) >= 0)) poster = null;
            var seasons = new System.Collections.Generic.List<int>();
            if (type == "tv")
            {
                if (!root.TryGetProperty("seasons", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 1000)
                    return new(null, null, SeerrFailure.UpstreamUnavailable);
                var seen = new System.Collections.Generic.HashSet<int>();
                foreach (var season in array.EnumerateArray())
                {
                    if (season.ValueKind != JsonValueKind.Object || !season.TryGetProperty("seasonNumber", out var number) ||
                        !number.TryGetInt32(out var value) || value is < 0 or > 1000 || !seen.Add(value))
                        return new(null, null, SeerrFailure.UpstreamUnavailable);
                    if (value > 0) seasons.Add(value);
                }
            }
            var info = root.TryGetProperty("mediaInfo", out var media) && media.ValueKind == JsonValueKind.Object ? media : default;
            int? status = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("status", out var state) &&
                state.ValueKind == JsonValueKind.Number && state.TryGetInt32(out var valueStatus) && valueStatus is >= 0 and <= 100 ? valueStatus : null;
            Guid? hint = null;
            if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("jellyfinMediaId", out var hinted) && hinted.ValueKind == JsonValueKind.String &&
                (Guid.TryParseExact(hinted.GetString(), "D", out var parsed) || Guid.TryParseExact(hinted.GetString(), "N", out parsed)) && parsed != Guid.Empty)
                hint = parsed;
            var normal = status != 6 && (permissions & (2 | 32 | (type == "movie" ? 262144 : 524288))) != 0;
            var fourK = status != 6 && (permissions & (2 | 1024 | (type == "movie" ? 2048 : 4096))) != 0;
            return new(new(title, Text(root, "overview", 500), poster, type, mediaId, status, null, normal, fourK, seasons.ToArray(), date), hint, null);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { return new(null, null, SeerrFailure.UpstreamUnavailable); }
        catch (HttpRequestException) { return new(null, null, SeerrFailure.UpstreamUnavailable); }
        catch (IOException) { return new(null, null, SeerrFailure.UpstreamUnavailable); }
        catch (JsonException) { return new(null, null, SeerrFailure.UpstreamUnavailable); }
        catch (InvalidOperationException) { return new(null, null, SeerrFailure.UpstreamUnavailable); }
    }

    /// <summary>Read-only request affordances, with fresh mapped permissions and validated TV season IDs.</summary>
    public async Task<RequestOptionsResult> GetRequestOptionsAsync(Guid userId, string type, int mediaId, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        if (_options is null) return new(null, SeerrFailure.Disabled);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var mapping = await GetAsync($"api/v1/user/jellyfin/{userId:D}", null, timeout.Token).ConfigureAwait(false);
            if (mapping.Status == HttpStatusCode.NotFound) return new(null, SeerrFailure.UserNotMapped);
            if (mapping.Status != HttpStatusCode.OK || mapping.Body is null) return new(null, SeerrFailure.UpstreamUnavailable);
            if (mapping.CsrfEnabled) return new(null, SeerrFailure.CsrfUnsupported);
            using var identity = JsonDocument.Parse(mapping.Body);
            var user = identity.RootElement;
            if (!user.TryGetProperty("id", out var id) || !id.TryGetInt32(out var mappedId) || mappedId <= 0 ||
                !user.TryGetProperty("permissions", out var bits) || !bits.TryGetInt32(out var permissions) || permissions < 0)
                return new(null, SeerrFailure.InvalidMapping);
            var normal = (permissions & (2 | 32 | (type == "movie" ? 262144 : 524288))) != 0;
            var fourK = (permissions & (2 | 1024 | (type == "movie" ? 2048 : 4096))) != 0;
            var detail = await GetAsync($"api/v1/{type}/{mediaId.ToString(CultureInfo.InvariantCulture)}", mappedId, timeout.Token).ConfigureAwait(false);
            if (detail.Status != HttpStatusCode.OK || detail.Body is null) return new(null, SeerrFailure.UpstreamUnavailable);
            using var json = JsonDocument.Parse(detail.Body);
            var root = json.RootElement;
            if (!root.TryGetProperty("id", out var detailId) || !detailId.TryGetInt32(out var actualId) || actualId != mediaId)
                return new(null, SeerrFailure.UpstreamUnavailable);
            var seasons = new System.Collections.Generic.List<int>();
            if (type == "tv")
            {
                if (!root.TryGetProperty("seasons", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 1000)
                    return new(null, SeerrFailure.UpstreamUnavailable);
                var seen = new System.Collections.Generic.HashSet<int>();
                foreach (var season in array.EnumerateArray())
                {
                    if (season.ValueKind != JsonValueKind.Object || !season.TryGetProperty("seasonNumber", out var number) ||
                        !number.TryGetInt32(out var value) || value < 0 || value > 1000 || !seen.Add(value))
                        return new(null, SeerrFailure.UpstreamUnavailable);
                    if (value > 0) seasons.Add(value);
                }
            }
            int? status = null, status4k = null;
            if (root.TryGetProperty("mediaInfo", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                if (info.TryGetProperty("status", out var state) && state.TryGetInt32(out var mediaStatus) && mediaStatus >= 0 && mediaStatus <= 100)
                    status = mediaStatus;
                if (info.TryGetProperty("status4k", out var state4k) && state4k.TryGetInt32(out var mediaStatus4k) && mediaStatus4k >= 0 && mediaStatus4k <= 100)
                    status4k = mediaStatus4k;
            }
            // Seerr v3.4.1 rejects both variants when the standard media status is BLOCKLISTED.
            if (status == 6) normal = fourK = false;
            return new(new(normal, fourK, status, status4k, seasons.ToArray()), null);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { return new(null, SeerrFailure.UpstreamUnavailable); }
        catch (HttpRequestException) { return new(null, SeerrFailure.UpstreamUnavailable); }
        catch (IOException) { return new(null, SeerrFailure.UpstreamUnavailable); }
        catch (JsonException) { return new(null, SeerrFailure.UpstreamUnavailable); }
        catch (InvalidOperationException) { return new(null, SeerrFailure.UpstreamUnavailable); }
    }

    /// <summary>Caller must pass a server-validated Jellyfin identity and a server-built catalog GET path.</summary>
    public Task<SeerrResult> GetUserReadAsync(Guid jellyfinUserId, string relativePath, CancellationToken cancellationToken)
    {
        if (!IsAllowedReadPath(relativePath)) throw new ArgumentException("Invalid Seerr read path.", nameof(relativePath));
        return GetMappedReadAsync(jellyfinUserId, _ => relativePath, cancellationToken);
    }

    /// <summary>Read pinned Seerr TV detail for age-rating verification; ID comes from a validated catalog candidate.</summary>
    public Task<SeerrResult> GetUserTvDetailAsync(Guid jellyfinUserId, int tmdbId, CancellationToken cancellationToken)
    {
        if (tmdbId is < 1 or > 100_000_000) throw new ArgumentOutOfRangeException(nameof(tmdbId));
        return GetMappedReadAsync(jellyfinUserId, _ => $"api/v1/tv/{tmdbId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
    }

    /// <summary>List only the validated Jellyfin user's own requests; the Seerr identity is resolved per call.</summary>
    public Task<SeerrResult> GetPersonalRequestsAsync(Guid validatedJellyfinUserId, int take, int skip, CancellationToken cancellationToken)
    {
        var boundedTake = Math.Clamp(take, 1, 100);
        var boundedSkip = Math.Clamp(skip, 0, 10_000);
        return GetMappedReadAsync(validatedJellyfinUserId,
            seerrUserId => $"api/v1/request?take={boundedTake}&skip={boundedSkip}&requestedBy={seerrUserId.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    /// <summary>Personal Home read: bind every returned record to the freshly mapped requester.</summary>
    public async Task<SeerrResult> GetPersonalAvailableRequestsAsync(Guid jellyfinUserId, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        if (_options is null) return SeerrResult.Fail(SeerrFailure.Disabled);
        if (jellyfinUserId == Guid.Empty) return SeerrResult.Fail(SeerrFailure.UserNotMapped);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(_timeout);
        var elapsed = Stopwatch.StartNew();
        var mapping = await MapAsync(jellyfinUserId, deadline.Token, callerToken).ConfigureAwait(false);
        if (mapping.Failure is { } failure) return SeerrResult.Fail(failure);
        var path = $"api/v1/request?take=100&skip=0&requestedBy={mapping.Id.ToString(CultureInfo.InvariantCulture)}";
        var result = await CachedReadAsync(jellyfinUserId, path, mapping.Id, mapping.Fingerprint,
            _timeout - elapsed.Elapsed, deadline.Token, callerToken).ConfigureAwait(false);
        if (!result.IsSuccess) return result;
        var root = result.Value!.Value;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var rows) ||
            rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 100) return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable);
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("requestedBy", out var requester) ||
                requester.ValueKind != JsonValueKind.Object || !requester.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var requestedBy) || requestedBy != mapping.Id)
                return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable);
        }
        return result;
    }

    /// <summary>Global server-only read; caller checks the existing Jellyfin user and administrator opt-in.</summary>
    public async Task<SeerrResult> GetSharedRequestsAsync(int page, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        if (_options is null) return SeerrResult.Fail(SeerrFailure.Disabled);
        if (page is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        if (!SharedReadSlots.Wait(0)) return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            deadline.CancelAfter(_timeout);
            // Seerr v3.4.1 scopes unprivileged lists to req.user.id. The server-only
            // API key resolves to admin ID 1 without X-API-User; this is not browser identity.
            var response = await GetAsync($"api/v1/request?take=20&skip={(page - 1) * 20}", null, deadline.Token).ConfigureAwait(false);
            if (response.Status != HttpStatusCode.OK || response.Body is null) return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable);
            using var json = JsonDocument.Parse(response.Body);
            return SeerrResult.Success(json.RootElement.Clone());
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable); }
        catch (HttpRequestException) { return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable); }
        catch (IOException) { return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable); }
        catch (JsonException) { return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable); }
        finally { SharedReadSlots.Release(); }
    }

    /// <summary>Rebind and check mapped permissions on every write. Never accepts a Seerr user ID or destination.</summary>
    public async Task<SeerrCreateResult> CreateRequestAsync(Guid validatedJellyfinUserId, CreateSeerrRequest selection, CancellationToken cancellationToken)
    {
        // Invalidate before starting (including concurrent reads) and again after the caller's POST.
        _readCache.Invalidate(validatedJellyfinUserId);
        try { return await CreateRequestCoreAsync(validatedJellyfinUserId, selection, cancellationToken).ConfigureAwait(false); }
        finally { _readCache.Invalidate(validatedJellyfinUserId); }
    }

    private async Task<SeerrCreateResult> CreateRequestCoreAsync(Guid validatedJellyfinUserId, CreateSeerrRequest selection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_options is null) return new(null, SeerrFailure.Disabled);
        if (validatedJellyfinUserId == Guid.Empty) return new(null, SeerrFailure.UserNotMapped);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var mapping = await GetAsync($"api/v1/user/jellyfin/{validatedJellyfinUserId:D}", null, timeout.Token).ConfigureAwait(false);
            if (mapping.Status == HttpStatusCode.NotFound) return new(null, SeerrFailure.UserNotMapped);
            if (mapping.Status != HttpStatusCode.OK || mapping.Body is null) return new(null, SeerrFailure.UpstreamUnavailable);
            // csurf runs before API-key authentication; a token cookie on this GET means a bare POST will fail.
            if (mapping.CsrfEnabled) return new(null, SeerrFailure.CsrfUnsupported);
            int mappedId, permissions;
            try
            {
                using var document = JsonDocument.Parse(mapping.Body);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out mappedId) || mappedId <= 0 ||
                    !root.TryGetProperty("permissions", out var bits) || !bits.TryGetInt32(out permissions) || permissions < 0)
                    return new(null, SeerrFailure.InvalidMapping);
            }
            catch (JsonException) { return new(null, SeerrFailure.InvalidMapping); }
            catch (InvalidOperationException) { return new(null, SeerrFailure.InvalidMapping); }

            // Seerr v3.4.1's hasPermission uses OR for type-specific request bits; admin bypasses.
            const int admin = 2;
            var needed = selection.MediaType == "movie"
                ? selection.Is4k ? 1024 | 2048 : 32 | 262144
                : selection.Is4k ? 1024 | 4096 : 32 | 524288;
            if ((permissions & (admin | needed)) == 0) return new(null, SeerrFailure.PermissionDenied);

            if (selection.MediaType == "tv")
            {
                var details = await GetAsync($"api/v1/tv/{selection.MediaId.ToString(CultureInfo.InvariantCulture)}", mappedId, timeout.Token).ConfigureAwait(false);
                if (details.CsrfEnabled) return new(null, SeerrFailure.CsrfUnsupported);
                if (details.Status != HttpStatusCode.OK || details.Body is null) return new(null, SeerrFailure.UpstreamUnavailable);
                using var document = JsonDocument.Parse(details.Body);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var showId) || !showId.TryGetInt32(out var actualId) || actualId != selection.MediaId ||
                    !root.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array || seasons.GetArrayLength() > 1000)
                    return new(null, SeerrFailure.UpstreamUnavailable);
                var available = new System.Collections.Generic.HashSet<int>();
                foreach (var season in seasons.EnumerateArray())
                {
                    if (season.ValueKind != JsonValueKind.Object || !season.TryGetProperty("seasonNumber", out var number) ||
                        !number.TryGetInt32(out var id) || id < 0 || id > 1000 || !available.Add(id))
                        return new(null, SeerrFailure.UpstreamUnavailable);
                }
                // Do not silently drop invalid choices. Seerr itself removes already-requested/available
                // seasons by 4K variant and returns 202 when none remain (including concurrent changes).
                if (selection.Seasons is null || Array.Exists(selection.Seasons, season => !available.Contains(season)))
                    return new(null, SeerrFailure.InvalidSeason);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUri, "api/v1/request"));
            request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
            request.Headers.TryAddWithoutValidation("X-API-User", mappedId.ToString(CultureInfo.InvariantCulture));
            // Serialize explicitly: no caller-supplied identity, Arr destination, profile, or quota override.
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                mediaType = selection.MediaType,
                mediaId = selection.MediaId,
                seasons = selection.Seasons,
                is4k = selection.Is4k
            }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
                System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Accepted) return new(null, SeerrFailure.AlreadyRequested);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(null, SeerrFailure.PermissionDenied);
            if (response.StatusCode != HttpStatusCode.Created || response.Content.Headers.ContentLength > MaxResponseBytes)
                return new(null, SeerrFailure.UpstreamUnavailable);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > MaxResponseBytes) return new(null, SeerrFailure.UpstreamUnavailable);
                buffer.Write(chunk, 0, count);
            }
            using var created = JsonDocument.Parse(buffer.ToArray());
            var value = created.RootElement;
            if (!value.TryGetProperty("id", out var createdId) || !createdId.TryGetInt32(out var requestId) || requestId <= 0 ||
                !value.TryGetProperty("status", out var createdStatus) || !createdStatus.TryGetInt32(out var status) || status < 0 || status > 100)
                return new(null, SeerrFailure.UpstreamUnavailable);
            return new(new(requestId, status), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, SeerrFailure.UpstreamUnavailable); }
        catch (HttpRequestException) { return new(null, SeerrFailure.UpstreamUnavailable); }
        catch (IOException) { return new(null, SeerrFailure.UpstreamUnavailable); }
        catch (JsonException) { return new(null, SeerrFailure.UpstreamUnavailable); }
        catch (InvalidOperationException) { return new(null, SeerrFailure.UpstreamUnavailable); }
    }

    public Task<SeerrBundle> GetDiscoveryAsync(Guid userId, int page, int take, CancellationToken cancellationToken) =>
        GetDiscoveryAsync(userId, page, page, page, take, cancellationToken);

    public async Task<SeerrBundle> GetDiscoveryAsync(Guid userId, int moviePage, int tvPage, int requestsPage, int take, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_options is null) return FailedBundle(SeerrFailure.Disabled);
        if (userId == Guid.Empty) return FailedBundle(SeerrFailure.UserNotMapped);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var elapsed = Stopwatch.StartNew();
        timeout.CancelAfter(_timeout);
        var mapping = await MapAsync(userId, timeout.Token, cancellationToken).ConfigureAwait(false);
        if (mapping.Failure is { } failure) return FailedBundle(failure);
        var id = mapping.Id;
        var boundedMovies = Math.Clamp(moviePage, 1, 100);
        var boundedTv = Math.Clamp(tvPage, 1, 100);
        var boundedRequests = Math.Clamp(requestsPage, 1, 100);
        var boundedTake = Math.Clamp(take, 1, 100);
        // Start all three reads after mapping; a stalled source must not prevent the others from starting.
        var movies = CachedReadAsync(userId, $"api/v1/discover/movies?page={boundedMovies}", id, mapping.Fingerprint, _timeout - elapsed.Elapsed, timeout.Token, cancellationToken);
        var tv = CachedReadAsync(userId, $"api/v1/discover/tv?page={boundedTv}", id, mapping.Fingerprint, _timeout - elapsed.Elapsed, timeout.Token, cancellationToken);
        var requests = CachedReadAsync(userId, $"api/v1/request?take={boundedTake}&skip={(boundedRequests - 1) * boundedTake}&requestedBy={id.ToString(CultureInfo.InvariantCulture)}", id, mapping.Fingerprint, _timeout - elapsed.Elapsed, timeout.Token, cancellationToken);
        await Task.WhenAll(movies, tv, requests).ConfigureAwait(false);
        return new SeerrBundle(movies.Result, tv.Result, requests.Result);
    }

    private static SeerrBundle FailedBundle(SeerrFailure failure)
    {
        var result = SeerrResult.Fail(failure);
        return new SeerrBundle(result, result, result);
    }

    private async Task<SeerrResult> GetMappedReadAsync(Guid jellyfinUserId, Func<int, string> pathForUser, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_options is null) return SeerrResult.Fail(SeerrFailure.Disabled);
        if (jellyfinUserId == Guid.Empty) return SeerrResult.Fail(SeerrFailure.UserNotMapped);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var elapsed = Stopwatch.StartNew();
        timeout.CancelAfter(_timeout);
        var mapping = await MapAsync(jellyfinUserId, timeout.Token, cancellationToken).ConfigureAwait(false);
        return mapping.Failure is { } failure ? SeerrResult.Fail(failure) :
            await CachedReadAsync(jellyfinUserId, pathForUser(mapping.Id), mapping.Id, mapping.Fingerprint, _timeout - elapsed.Elapsed, timeout.Token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SeerrResult> CachedReadAsync(Guid jellyfinUserId, string path, int mappedId, string fingerprint, TimeSpan remaining, CancellationToken deadlineToken, CancellationToken callerToken)
    {
        try
        {
            deadlineToken.ThrowIfCancellationRequested();
            return await _readCache.GetAsync(_cacheScope + fingerprint, jellyfinUserId, mappedId, path,
                token => ReadMappedAsync(path, mappedId, token, CancellationToken.None), remaining, deadlineToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable);
        }
    }

    private Task<(int Id, string Fingerprint, SeerrFailure? Failure)> MapAsync(Guid userId, CancellationToken token, CancellationToken callerToken) =>
        _readCache.WithMappingSlotAsync(() => MapCoreAsync(userId, token, callerToken),
            (0, string.Empty, SeerrFailure.UpstreamUnavailable));

    private async Task<(int Id, string Fingerprint, SeerrFailure? Failure)> MapCoreAsync(Guid userId, CancellationToken token, CancellationToken callerToken)
    {
        try
        {
            var mapping = await GetAsync($"api/v1/user/jellyfin/{userId:D}", null, token).ConfigureAwait(false);
            if (mapping.Status == HttpStatusCode.NotFound) return (0, string.Empty, SeerrFailure.UserNotMapped);
            if (mapping.Status != HttpStatusCode.OK || mapping.Body is null) return (0, string.Empty, SeerrFailure.UpstreamUnavailable);
            try
            {
                using var json = JsonDocument.Parse(mapping.Body);
                if (!json.RootElement.TryGetProperty("id", out var id) || !id.TryGetInt32(out var value) || value <= 0)
                    return (0, string.Empty, SeerrFailure.InvalidMapping);
                // The fresh mapping snapshot (including permissions) is part of cache identity.
                // Hash rather than retaining mapping fields or credentials in the cache key.
                return (value, Convert.ToHexString(SHA256.HashData(mapping.Body)), null);
            }
            catch (JsonException) { return (0, string.Empty, SeerrFailure.InvalidMapping); }
            catch (InvalidOperationException) { return (0, string.Empty, SeerrFailure.InvalidMapping); }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { return (0, string.Empty, SeerrFailure.UpstreamUnavailable); }
        catch (HttpRequestException) { return (0, string.Empty, SeerrFailure.UpstreamUnavailable); }
        catch (IOException) { return (0, string.Empty, SeerrFailure.UpstreamUnavailable); }
    }

    private async Task<SeerrResult> ReadMappedAsync(string path, int userId, CancellationToken token, CancellationToken callerToken)
    {
        try
        {
            var response = await GetAsync(path, userId, token).ConfigureAwait(false);
            if (response.Status != HttpStatusCode.OK || response.Body is null) return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable);
            using var json = JsonDocument.Parse(response.Body);
            return SeerrResult.Success(json.RootElement.Clone());
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable); }
        catch (HttpRequestException) { return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable); }
        catch (IOException) { return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable); }
        catch (JsonException) { return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable); }
    }

    private static bool IsAllowedReadPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('#') || path.Contains('\\') || path.IndexOfAny(['\r', '\n']) >= 0) return false;
        var route = path.Split('?', 2)[0];
        return route is "api/v1/search" or "api/v1/discover/trending" or "api/v1/discover/movies" or "api/v1/discover/tv";
    }

    private async Task<(HttpStatusCode Status, byte[]? Body, bool CsrfEnabled)> GetAsync(string path, int? userId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options!.BaseUri, path));
        request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
        if (userId.HasValue) request.Headers.TryAddWithoutValidation("X-API-User", userId.Value.ToString(CultureInfo.InvariantCulture));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var csrf = response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            System.Linq.Enumerable.Any(cookies, cookie => cookie.StartsWith("XSRF-TOKEN=", StringComparison.OrdinalIgnoreCase));
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null, csrf);
        if (response.Content.Headers.ContentLength > MaxResponseBytes) return (HttpStatusCode.RequestEntityTooLarge, null, csrf);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxResponseBytes) return (HttpStatusCode.RequestEntityTooLarge, null, csrf);
            buffer.Write(chunk, 0, count);
        }
        return (response.StatusCode, buffer.ToArray(), csrf);
    }
}
