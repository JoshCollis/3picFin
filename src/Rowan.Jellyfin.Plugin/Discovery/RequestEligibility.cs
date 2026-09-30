using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Rowan.Jellyfin.Plugin.Discovery;

/// <summary>Seerr v3.4.1 media/request semantics. No requester data leaves this policy.</summary>
internal static class RequestEligibility
{
    internal sealed record Result(bool Normal, bool FourK, int? Status, int? Status4k, int[] Seasons, int[] Seasons4k);

    internal static Result Evaluate(JsonElement root, string type)
    {
        var all = new HashSet<int>();
        if (type == "tv")
            foreach (var season in Array(root, "seasons"))
            {
                var n = Number(season, "seasonNumber");
                if (n is null or < 0 or > 1000 || !all.Add(n.Value)) throw new JsonException();
            }
        all.Remove(0);
        // An absent media record is the upstream representation of an untracked title.
        if (!root.TryGetProperty("mediaInfo", out var info) || info.ValueKind == JsonValueKind.Null)
            return new(type == "movie" || all.Count > 0, type == "movie" || all.Count > 0, null, null, all.Order().ToArray(), all.Order().ToArray());
        if (info.ValueKind != JsonValueKind.Object) throw new JsonException();
        var status = Number(info, "status");
        var status4k = Number(info, "status4k");
        if (status == 6) return new(false, false, status, status4k, [], []);
        var normal = status is 1 or 7;
        var fourK = status4k is 1 or 7;
        var normalSeasons = new HashSet<int>(all);
        var fourKSeasons = new HashSet<int>(all);
        if (type == "tv")
        {
            // Series status cannot reserve newly added seasons. Incomplete per-season
            // data is an eligibility failure, never permission to submit.
            normal = status is >= 1 and <= 7;
            fourK = status4k is >= 1 and <= 7 && status4k != 6;
            var seen = new HashSet<int>();
            foreach (var season in Array(info, "seasons"))
            {
                var n = Number(season, "seasonNumber");
                if (n is null or < 0 or > 1000 || !seen.Add(n.Value)) throw new JsonException();
                if (Number(season, "status") is not (1 or 7)) normalSeasons.Remove(n.Value);
                if (Number(season, "status4k") is not (1 or 7)) fourKSeasons.Remove(n.Value);
            }
        }
        foreach (var request in Array(info, "requests"))
        {
            var requestStatus = Number(request, "status");
            if (requestStatus is not (>= 1 and <= 5) || !request.TryGetProperty("is4k", out var variant) ||
                variant.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
            // Declined and completed do not reserve a new request. FAILED does;
            // retrying a failed request belongs to Seerr's existing-request workflow.
            if (requestStatus is 3 or 5) continue;
            var is4k = variant.GetBoolean();
            if (type == "movie") { if (is4k) fourK = false; else normal = false; }
            else foreach (var season in Array(request, "seasons"))
            {
                var n = Number(season, "seasonNumber");
                if (n is null or < 0 or > 1000) throw new JsonException();
                (is4k ? fourKSeasons : normalSeasons).Remove(n.Value);
            }
        }
        return new(normal && (type == "movie" || normalSeasons.Count > 0),
            fourK && (type == "movie" || fourKSeasons.Count > 0), status, status4k,
            normal ? normalSeasons.Order().ToArray() : [], fourK ? fourKSeasons.Order().ToArray() : []);
    }

    private static int? Number(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
    private static JsonElement.ArrayEnumerator Array(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 1000) throw new JsonException();
        return value.EnumerateArray();
    }
}
