using System;
using System.Collections.Generic;
using System.Globalization;

namespace Rowan.Jellyfin.Plugin.Assistant;

/// <summary>Fixed Seerr discovery routes and TMDb genre IDs; no model-controlled URLs or query keys.</summary>
public static class AiCatalog
{
    public static object GenreNames => new { movie = MovieGenres.Keys, tv = TvGenres.Keys };
    private static readonly Dictionary<string, int> MovieGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = 28, ["Adventure"] = 12, ["Animation"] = 16, ["Comedy"] = 35, ["Crime"] = 80,
        ["Documentary"] = 99, ["Drama"] = 18, ["Family"] = 10751, ["Fantasy"] = 14, ["History"] = 36,
        ["Horror"] = 27, ["Music"] = 10402, ["Mystery"] = 9648, ["Romance"] = 10749,
        ["Science Fiction"] = 878, ["TV Movie"] = 10770, ["Thriller"] = 53, ["War"] = 10752, ["Western"] = 37
    };
    private static readonly Dictionary<string, int> TvGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action & Adventure"] = 10759, ["Animation"] = 16, ["Comedy"] = 35, ["Crime"] = 80,
        ["Documentary"] = 99, ["Drama"] = 18, ["Family"] = 10751, ["Kids"] = 10762, ["Mystery"] = 9648,
        ["News"] = 10763, ["Reality"] = 10764, ["Sci-Fi & Fantasy"] = 10765, ["Soap"] = 10766,
        ["Talk"] = 10767, ["War & Politics"] = 10768, ["Western"] = 37
    };

    public static (string Path, string? Type, DateOnly? From, DateOnly? To) Browse(string feed, int page, string genre,
        string? releasedAfter, string? releasedBefore, DateOnly today)
    {
        var type = feed switch
        {
            "trending" => null, "movies" or "recent_movies" or "upcoming_movies" => "movie",
            "tv" or "recent_tv" or "upcoming_tv" => "tv", _ => throw new FormatException()
        };
        DateOnly? Date(string? value) => value is null ? null :
            DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw new FormatException();
        var from = Date(releasedAfter); var to = Date(releasedBefore);
        if (type is null && (genre.Length > 0 || from.HasValue || to.HasValue)) throw new FormatException();
        if (feed.StartsWith("recent_", StringComparison.Ordinal)) { from ??= today.AddDays(-180); to ??= today; }
        if (feed.StartsWith("upcoming_", StringComparison.Ordinal)) from ??= today;
        if (from.HasValue && to.HasValue && from > to) throw new FormatException();
        var path = $"api/v1/discover/{(type == "movie" ? "movies" : type == "tv" ? "tv" : "trending")}?page={page}&sortBy=popularity.desc";
        if (genre.Length > 0)
        {
            if (!(type == "movie" ? MovieGenres : TvGenres).TryGetValue(genre, out var id)) throw new FormatException();
            path += "&genre=" + id.ToString(CultureInfo.InvariantCulture);
        }
        var prefix = type == "movie" ? "primaryReleaseDate" : "firstAirDate";
        if (from.HasValue) path += $"&{prefix}Gte={from:yyyy-MM-dd}";
        if (to.HasValue) path += $"&{prefix}Lte={to:yyyy-MM-dd}";
        return (path, type, from, to);
    }
}
