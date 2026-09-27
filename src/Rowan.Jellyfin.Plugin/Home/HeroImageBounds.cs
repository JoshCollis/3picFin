using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Bounded JPEG pass-through: no decoder or original-file fallback is involved.</summary>
public static class HeroImageBounds
{
    public const int MaxBytes = 8 * 1024 * 1024;
    private const long MaxPixels = 8_294_400;

    public static async Task<byte[]?> TryReadJpeg(Stream stream, CancellationToken cancellation)
    {
        if (!stream.CanSeek || stream.Length < 23 || stream.Length > MaxBytes) return null;
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
        if (bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[^2] != 0xff || bytes[^1] != 0xd9) return null;
        var pos = 2;
        var dimensions = false;
        while (pos + 4 <= bytes.Length)
        {
            if (bytes[pos++] != 0xff) return null;
            while (pos < bytes.Length && bytes[pos] == 0xff) pos++;
            if (pos >= bytes.Length) return null;
            var marker = bytes[pos++];
            if (marker == 0xda) return dimensions ? bytes : null; // scan payload is opaque
            if (marker is 0xd8 or 0xd9 or 0x00 or >= 0xd0 and <= 0xd7) return null;
            if (pos + 2 > bytes.Length) return null;
            var length = (bytes[pos] << 8) | bytes[pos + 1];
            if (length < 2 || pos + length > bytes.Length) return null;
            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                if (length < 8) return null;
                var height = (bytes[pos + 3] << 8) | bytes[pos + 4];
                var width = (bytes[pos + 5] << 8) | bytes[pos + 6];
                if (width == 0 || height == 0 || (long)width * height > MaxPixels) return null;
                dimensions = true;
            }
            pos += length;
        }
        return dimensions && pos == bytes.Length - 2 ? bytes : null;
    }
}

/// <summary>Fixed-size rate slots and active-user set; no unbounded per-user history.</summary>
public sealed class HeroImageGate
{
    private readonly object _lock = new();
    private readonly HashSet<Guid> _activeUsers = [];
    private readonly (Guid User, long Window, int Count)[] _slots = new (Guid, long, int)[256];
    private readonly int _capacity;
    private readonly Func<long> _tickCount;
    private long _globalWindow;
    private int _globalCount;
    public HeroImageGate(int capacity = 4, Func<long>? tickCount = null)
    {
        _capacity = capacity;
        _tickCount = tickCount ?? (() => Environment.TickCount64);
    }
    public int Active { get { lock (_lock) return _activeUsers.Count; } }
    public IDisposable? TryEnter(Guid user)
    {
        lock (_lock)
        {
            var window = _tickCount() / 1000;
            if (_globalWindow != window) { _globalWindow = window; _globalCount = 0; }
            var slot = (int)((uint)user.GetHashCode() % (uint)_slots.Length);
            var prior = _slots[slot];
            if (_activeUsers.Contains(user) || _activeUsers.Count >= _capacity || _globalCount >= 16 ||
                (prior.Window == window && (prior.User != user || prior.Count >= 4))) return null;
            _slots[slot] = (user, window, prior.Window == window && prior.User == user ? prior.Count + 1 : 1);
            _globalCount++;
            _activeUsers.Add(user);
            return new Lease(this, user);
        }
    }
    private sealed class Lease(HeroImageGate gate, Guid user) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) lock (gate._lock) gate._activeUsers.Remove(user); }
    }
}
