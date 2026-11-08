using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Rowan.Jellyfin.Plugin.Assistant;

/// <summary>Account-keyed local storage, never a client-selected path. No conversation/provider IDs are shared.</summary>
public sealed class AiStore(string directory)
{
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private const int MaxBytes = 2 * 1024 * 1024;
    private string FileName(Guid user) => Path.Combine(directory, user.ToString("N") + ".json");

    public async Task<T> Use<T>(Guid user, Func<AiUserData, Task<T>> action, CancellationToken token)
    {
        if (user == Guid.Empty) throw new AiException("Sign in to use the assistant.", 403);
        var slot = _locks[(user.GetHashCode() & int.MaxValue) % _locks.Length];
        if (!await slot.WaitAsync(0, token).ConfigureAwait(false)) throw new AiException("An operation is already in progress. Try again shortly.", 429);
        try
        {
            var data = await Load(user, token).ConfigureAwait(false);
            Prune(data, DateTimeOffset.UtcNow);
            var result = await action(data).ConfigureAwait(false);
            await Save(user, data, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally { slot.Release(); }
    }

    private async Task<AiUserData> Load(Guid user, CancellationToken token)
    {
        var path = FileName(user);
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > MaxBytes) throw new AiException("Chat storage is unavailable.", 503);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<AiUserData>(bytes, AiJson.Options) ?? throw new JsonException();
            if (data.Threads is null || data.Memory is null || data.Memory.Any(m => m is null) ||
                data.Threads.Any(t => t is null || t.Messages is null || t.Messages.Any(m => m is null || m.Cards is null || m.Proposals is null ||
                    m.Proposals.Any(p => p is null || p.Card is null || p.Selection is null)))) throw new JsonException();
            return data;
        }
        catch (JsonException) { throw new AiException("Chat storage is unavailable. Ask your administrator to check it.", 503); }
    }

    // Call only while holding this user's lock. Persist 'submitting' BEFORE any Seerr write.
    public async Task Save(Guid user, AiUserData data, CancellationToken token)
    {
        Prune(data, DateTimeOffset.UtcNow);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, AiJson.Options);
        while (bytes.Length > MaxBytes && data.Threads.Count > 1)
        {
            data.Threads.Remove(data.Threads.MinBy(t => t.UpdatedAt)!);
            bytes = JsonSerializer.SerializeToUtf8Bytes(data, AiJson.Options);
        }
        if (bytes.Length > MaxBytes) throw new AiException("Chat storage is full. Delete an older conversation.", 409);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = FileName(user);
        var temporary = path + ".tmp";
        try
        {
            // Never make chat files world-readable, including during the atomic replacement.
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, fileOptions)) await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Prune(AiUserData data, DateTimeOffset now)
    {
        data.Threads.RemoveAll(t => t.UpdatedAt < now.AddDays(-30));
        data.Threads = data.Threads.OrderByDescending(t => t.UpdatedAt).Take(8).ToList();
        foreach (var thread in data.Threads) thread.Messages = thread.Messages.TakeLast(30).ToList();
        data.Memory = data.Memory.TakeLast(20).ToList();
    }

    public async Task Sweep(CancellationToken token)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var user)) continue;
            try { await Use(user, data => Task.FromResult(0), token).ConfigureAwait(false); }
            catch (AiException) { /* Busy or damaged account: leave it intact, never log its contents. */ }
            catch (IOException) { /* Retry on the next sweep. */ }
        }
    }
}

public sealed class AiRetention(AiStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await store.Sweep(stoppingToken).ConfigureAwait(false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
