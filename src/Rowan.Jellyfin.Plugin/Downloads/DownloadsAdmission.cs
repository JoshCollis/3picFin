using System;
using System.Collections.Generic;

namespace Rowan.Jellyfin.Plugin.Downloads;

/// <summary>Non-queuing admission shared by all requests; per-user and global in-flight bounds.</summary>
public sealed class DownloadsAdmission(int globalLimit = 4, int perUserLimit = 1)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _users = new();
    private int _active;

    public IDisposable? TryEnter(Guid user)
    {
        lock (_gate)
        {
            _users.TryGetValue(user, out var count);
            if (user == Guid.Empty || _active >= globalLimit || count >= perUserLimit) return null;
            _active++;
            _users[user] = count + 1;
            return new Lease(this, user);
        }
    }

    private sealed class Lease(DownloadsAdmission owner, Guid user) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                owner._active--;
                if (--owner._users[user] == 0) owner._users.Remove(user);
            }
        }
    }
}
