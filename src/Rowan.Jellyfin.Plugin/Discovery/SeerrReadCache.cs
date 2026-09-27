using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Rowan.Jellyfin.Plugin.Discovery;

/// <summary>Bounded, short-lived successful read snapshots. Mapping is intentionally outside this cache.</summary>
public sealed class SeerrReadCache
{
    private const int MaxEntries = 32;
    private const int MaxCachedCharacters = 65_536;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _upstreamSlots = new(32, 32);
    private readonly Dictionary<(string Scope, Guid User, int MappedId, string Path, long Epoch), Entry> _entries = new();
    private readonly Dictionary<Guid, long> _epochs = new();
    private readonly Dictionary<Guid, int> _activeReaders = new();
    private sealed class Entry(Task<SeerrResult> task, DateTimeOffset created, CancellationTokenSource limit, DateTimeOffset deadline)
    {
        public Task<SeerrResult> Task { get; } = task;
        public DateTimeOffset Created { get; } = created;
        public CancellationTokenSource Limit { get; } = limit;
        public DateTimeOffset Deadline { get; set; } = deadline;
        public int Waiters { get; set; }
    }

    public SeerrReadCache(TimeSpan? ttl = null) => _ttl = ttl is { } value && value > TimeSpan.Zero && value <= TimeSpan.FromSeconds(2)
        ? value : TimeSpan.FromSeconds(2);

    public void Invalidate(Guid user)
    {
        lock (_gate)
        {
            _epochs[user] = _epochs.GetValueOrDefault(user) + 1;
            foreach (var key in new List<(string Scope, Guid User, int MappedId, string Path, long Epoch)>(_entries.Keys))
                if (key.User == user) _entries.Remove(key);
            // Epochs must be bounded as well: only users with live entries need a generation.
            if (_epochs.Count > 256)
                foreach (var id in new List<Guid>(_epochs.Keys))
                    if (!_entries.Keys.AnyUser(id) && !_activeReaders.ContainsKey(id) && id != user) _epochs.Remove(id);
        }
    }

    public async Task<SeerrResult> GetAsync(string scope, Guid user, int mappedId, string path,
        Func<CancellationToken, Task<SeerrResult>> load, TimeSpan timeout, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        lock (_gate) _activeReaders[user] = _activeReaders.GetValueOrDefault(user) + 1;
        try
        {
            while (true)
            {
                caller.ThrowIfCancellationRequested();
                Entry entry;
                long epoch;
                (string Scope, Guid User, int MappedId, string Path, long Epoch) key;
                lock (_gate)
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var stale in new List<(string Scope, Guid User, int MappedId, string Path, long Epoch)>(_entries.Keys))
                        if (_entries[stale].Task.IsCompleted && now - _entries[stale].Created >= _ttl) _entries.Remove(stale);
                    epoch = _epochs.GetValueOrDefault(user);
                    key = (scope, user, mappedId, path, epoch);
                    if (_entries.TryGetValue(key, out var existing) && !existing.Limit.IsCancellationRequested)
                    {
                        entry = existing;
                        var deadline = now + timeout;
                        if (!entry.Task.IsCompleted && deadline > entry.Deadline)
                        {
                            entry.Deadline = deadline;
                            entry.Limit.CancelAfter(deadline - now);
                        }
                    }
                    else
                    {
                        if (existing is not null) _entries.Remove(key);
                        if (_entries.Count >= MaxEntries || !_upstreamSlots.Wait(0))
                            return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable);
                        var limit = new CancellationTokenSource(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero);
                        var task = LoadAsync(load, limit);
                        entry = new Entry(task, now, limit, now + timeout);
                        _entries.Add(key, entry);
                        _ = RemoveFailureAsync(key, task, limit);
                    }
                    entry.Waiters++;
                }
                try
                {
                    var result = await entry.Task.WaitAsync(caller).ConfigureAwait(false);
                    lock (_gate)
                    {
                        caller.ThrowIfCancellationRequested();
                        if (_epochs.GetValueOrDefault(user) == epoch) return result;
                    }
                }
                finally
                {
                    bool abandon;
                    lock (_gate)
                    {
                        abandon = --entry.Waiters == 0 && !entry.Task.IsCompleted;
                        if (abandon && _entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                            _entries.Remove(key);
                    }
                    if (abandon)
                    {
                        // Completion can dispose the limit after we drop the lock; then no work remains.
                        try { entry.Limit.Cancel(); }
                        catch (ObjectDisposedException) when (entry.Task.IsCompleted) { }
                    }
                }
                // A POST invalidated the snapshot while this caller waited. Never serve it.
            }
        }
        finally
        {
            lock (_gate)
            {
                if (--_activeReaders[user] == 0) _activeReaders.Remove(user);
            }
        }
    }

    /// <summary>Mapping and catalog reads compete for the same bounded upstream budget.</summary>
    public async Task<T> WithMappingSlotAsync<T>(Func<Task<T>> load, T unavailable)
    {
        if (!_upstreamSlots.Wait(0)) return unavailable;
        try { return await load().ConfigureAwait(false); }
        finally { _upstreamSlots.Release(); }
    }

    private async Task<SeerrResult> LoadAsync(Func<CancellationToken, Task<SeerrResult>> load, CancellationTokenSource limit)
    {
        // The slot was admitted under the cache lock; do not start HTTP under that lock.
        await Task.Yield();
        try
        {
            return await load(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            return SeerrResult.Fail(SeerrFailure.UpstreamUnavailable);
        }
        finally { _upstreamSlots.Release(); }
    }

    private async Task RemoveFailureAsync((string Scope, Guid User, int MappedId, string Path, long Epoch) key, Task<SeerrResult> task, CancellationTokenSource limit)
    {
        bool keep;
        try
        {
            var result = await task.ConfigureAwait(false);
            keep = result.IsSuccess && result.Value?.GetRawText().Length <= MaxCachedCharacters;
        }
        catch { keep = false; }
        lock (_gate)
        {
            if (!keep && _entries.TryGetValue(key, out var entry) && ReferenceEquals(entry.Task, task)) _entries.Remove(key);
            limit.Dispose();
        }
    }
}

internal static class CacheKeyExtensions
{
    internal static bool AnyUser(this IEnumerable<(string Scope, Guid User, int MappedId, string Path, long Epoch)> keys, Guid user)
    {
        foreach (var key in keys) if (key.User == user) return true;
        return false;
    }
}