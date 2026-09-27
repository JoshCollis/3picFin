using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Per-request selection; never caches or trusts a similar-items provider as an entitlement boundary.</summary>
public static class BecauseYouWatchedPolicy
{
    /// <summary>Pick distinct visible played movies without repeating a collection across headings.</summary>
    public static Movie[] SelectSeeds(IEnumerable<BaseItem> candidates, IReadOnlyCollection<Guid> visibleLibraries,
        Func<BaseItem, bool> visible, Func<Movie, IReadOnlyCollection<Guid>> collections, int limit)
    {
        if (visibleLibraries.Count == 0 || limit <= 0) return [];
        var result = new List<Movie>();
        var seen = new HashSet<Guid>();
        var usedCollections = new HashSet<Guid>();
        foreach (var item in candidates)
        {
            if (item is not Movie movie || item.Id == Guid.Empty || !seen.Add(item.Id) ||
                !item.GetAncestorIds().Any(visibleLibraries.Contains) || !visible(item)) continue;
            var memberships = collections(movie);
            if (memberships.Count > 32 || memberships.Any(usedCollections.Contains)) continue;
            result.Add(movie);
            usedCollections.UnionWith(memberships);
            if (result.Count == limit) break;
        }
        return result.ToArray();
    }

    /// <summary>Gather bounded visible candidates from each library before choosing diverse headings.</summary>
    public static Movie[] CollectSeeds(IReadOnlyCollection<Guid> libraries,
        Func<Guid, int, int, BaseItem[]> fetch, Func<BaseItem, bool> visible,
        Func<Movie, IReadOnlyCollection<Guid>> collections, int pageSize, int limit)
    {
        if (libraries.Count == 0 || pageSize <= 0 || limit <= 0) return [];
        var pools = new List<List<(Movie Movie, IReadOnlyCollection<Guid> Memberships)>>();
        var seen = new HashSet<Guid>();
        foreach (var library in libraries.Take(8))
        {
            var pool = new List<(Movie Movie, IReadOnlyCollection<Guid> Memberships)>();
            var localCollections = new HashSet<Guid>();
            pools.Add(pool);
            for (var page = 0; page < 8 && pool.Count < limit; page++)
            {
                var batch = fetch(library, page * pageSize, pageSize);
                foreach (var item in batch.Take(pageSize))
                {
                    if (item is not Movie movie || item.Id == Guid.Empty || !seen.Add(item.Id) ||
                        !item.GetAncestorIds().Contains(library) || !visible(item)) continue;
                    var memberships = collections(movie);
                    if (memberships.Count > 32 || memberships.Any(localCollections.Contains)) continue;
                    pool.Add((movie, memberships));
                    localCollections.UnionWith(memberships);
                    if (pool.Count == limit) break;
                }
                if (batch.Length < pageSize) break;
            }
        }
        // Rotate library priority per request; a fixed first five would starve later roots.
        var shuffledPools = pools.ToArray();
        Random.Shared.Shuffle(shuffledPools);
        var selected = new List<Movie>();
        var usedCollections = new HashSet<Guid>();
        for (var index = 0; index < limit && selected.Count < limit; index++)
        {
            foreach (var pool in shuffledPools)
            {
                if (index >= pool.Count || pool[index].Memberships.Any(usedCollections.Contains)) continue;
                selected.Add(pool[index].Movie);
                usedCollections.UnionWith(pool[index].Memberships);
                if (selected.Count == limit) break;
            }
        }
        return selected.ToArray();
    }

    /// <summary>Run each seed independently, validating live entitlement before and after the provider.</summary>
    public static async Task<IReadOnlyList<(Movie Seed, Movie[] Items)>> GatherResultsAsync(
        IEnumerable<Movie> seeds, Func<Guid[]> visibleLibraries, Func<BaseItem, bool> visible,
        Func<BaseItem, bool> played, Func<Movie, CancellationToken, Task<IEnumerable<BaseItem>>> similar,
        int limit, bool hideWatched, CancellationToken cancellationToken)
    {
        var rows = new List<(Movie Seed, Movie[] Items)>();
        foreach (var seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool Allowed(Guid[] roots) => roots.Length > 0 &&
                seed.GetAncestorIds().Any(roots.Contains) && visible(seed) && played(seed);
            // Candidate selection is only a snapshot: recheck roots and ancestry at the provider boundary.
            if (!Allowed(visibleLibraries())) continue;
            IEnumerable<BaseItem> candidates;
            try
            {
                candidates = await similar(seed, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // A broken provider must not discard rows already gathered or leak its exception to HTTP.
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var roots = visibleLibraries();
            if (!Allowed(roots)) continue;
            rows.Add((seed, SelectResults(seed, candidates, roots, visible, played, limit, hideWatched)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return rows;
    }

    /// <summary>Convert bounded rows, then revalidate every seed and card against current entitlement.</summary>
    public static IReadOnlyList<(Movie Seed, T SeedDto, IReadOnlyList<T> Items)> FinalizeRows<T>(
        IReadOnlyList<(Movie Seed, Movie[] Items)> gathered, Func<Guid[]> visibleLibraries,
        Func<BaseItem, bool> visible, Func<BaseItem, bool> played,
        Func<Movie, T> seedDto, Func<Movie, T> cardDto, CancellationToken cancellationToken)
    {
        var converted = new List<(Movie Seed, T SeedDto, (Movie Item, T Dto)[] Cards)>();
        foreach (var (seed, items) in gathered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool SeedAllowed(Guid[] roots) => roots.Length > 0 &&
                seed.GetAncestorIds().Any(roots.Contains) && visible(seed) && played(seed);
            var roots = visibleLibraries();
            if (!SeedAllowed(roots)) continue;
            var heading = seedDto(seed);
            var cards = items.Where(item => item.GetAncestorIds().Any(roots.Contains) && visible(item))
                .Select(item => (Item: item, Dto: cardDto(item))).ToArray();
            converted.Add((seed, heading, cards));
        }

        // This pass must follow *all* DTO conversions: a later conversion can revoke an earlier row.
        var result = new List<(Movie Seed, T SeedDto, IReadOnlyList<T> Items)>();
        foreach (var (seed, heading, cards) in converted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var roots = visibleLibraries();
            if (roots.Length == 0 || !seed.GetAncestorIds().Any(roots.Contains) || !visible(seed) || !played(seed)) continue;
            var valid = new List<T>();
            foreach (var (item, dto) in cards)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.GetAncestorIds().Any(roots.Contains) && visible(item)) valid.Add(dto);
            }
            if (valid.Count > 0) result.Add((seed, heading, valid));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Keep provider order while enforcing current user/library/parent entitlement and optional played filter.</summary>
    public static Movie[] SelectResults(Movie seed, IEnumerable<BaseItem> candidates,
        IReadOnlyCollection<Guid> visibleLibraries, Func<BaseItem, bool> visible,
        Func<BaseItem, bool> played, int limit, bool hideWatched = false)
    {
        if (visibleLibraries.Count == 0 || limit <= 0 || !seed.GetAncestorIds().Any(visibleLibraries.Contains) || !visible(seed)) return [];
        return candidates.Take(64).OfType<Movie>()
            .Where(item => item.Id != Guid.Empty && item.Id != seed.Id &&
                item.GetAncestorIds().Any(visibleLibraries.Contains) && visible(item) &&
                (!hideWatched || !played(item)))
            .DistinctBy(item => item.Id).Take(limit).ToArray();
    }
}
