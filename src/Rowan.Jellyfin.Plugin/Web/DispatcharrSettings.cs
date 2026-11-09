using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Home;

namespace Rowan.Jellyfin.Plugin.Web;

public sealed class DispatcharrSettings(bool enabled, string baseUrl, string revision, DispatcharrConnection? connection)
{
    public bool Enabled { get; } = enabled;
    public string BaseUrl { get; } = baseUrl;
    public string Revision { get; } = revision;
    [JsonIgnore] public DispatcharrConnection? Connection { get; } = connection;
    public override string ToString() => "DispatcharrSettings { [REDACTED] }";
}
public sealed record DispatcharrSettingsView(bool Enabled, string BaseUrl, bool HasApiKey, string Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DispatcharrSettingsUpdate(bool Enabled, string BaseUrl, string? ApiKey, bool ClearApiKey, string Revision)
{ public override string ToString() => "DispatcharrSettingsUpdate { [REDACTED] }"; }

/// <summary>Separate server-only secret store: ordinary plugin configuration GET/POST never carries or clears this key.</summary>
public sealed class DispatcharrSettingsStore(string path)
{
    private readonly object _sync = new();
    private Stored? _stored;
    private DispatcharrSettings? _current;
    private sealed record Stored(bool Enabled, string BaseUrl, string? ApiKey, string Revision)
    { public override string ToString() => "DispatcharrStored { [REDACTED] }"; }
    public DispatcharrSettings Snapshot()
    {
        lock (_sync)
        {
            if (_current is not null) return _current;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 8192) throw new InvalidDataException();
                using var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                if (file.Length > 8192) throw new InvalidDataException();
                var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
                if (file.Length != bytes.Length) throw new InvalidDataException();
                using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
                DispatcharrClient.UniqueProperties(doc.RootElement);
                _stored = JsonSerializer.Deserialize<Stored>(bytes) ?? throw new InvalidDataException();
            }
            else _stored = new(false, "", null, Guid.NewGuid().ToString("N"));
            return _current = Resolve(_stored);
        }
    }
    private static DispatcharrSettings Resolve(Stored settings)
    {
        if (!Guid.TryParseExact(settings.Revision,"N",out _) || settings.BaseUrl is null) throw new InvalidDataException();
        var connection = !string.IsNullOrEmpty(settings.ApiKey) && !string.IsNullOrEmpty(settings.BaseUrl)
            ? DispatcharrConnection.Parse(settings.BaseUrl, settings.ApiKey) : null;
        if (settings.Enabled && connection is null) throw new InvalidDataException();
        return new(settings.Enabled, settings.BaseUrl, settings.Revision, connection);
    }
    public bool IsCurrent(DispatcharrSettings settings) { lock (_sync) return ReferenceEquals(settings, Snapshot()); }
    public DispatcharrSettingsView View()
    { lock (_sync) { var s = Snapshot(); return new(s.Enabled,s.BaseUrl,!string.IsNullOrEmpty(_stored!.ApiKey),s.Revision); } }
    public DispatcharrSettingsView Save(DispatcharrSettingsUpdate update)
    {
        lock (_sync)
        {
            var current = Snapshot();
            if (update.Revision != current.Revision) throw new InvalidOperationException("Settings changed. Reload before saving.");
            if (update.BaseUrl is null || update.ApiKey?.Length > 512 || update.ClearApiKey && !string.IsNullOrEmpty(update.ApiKey)) throw new InvalidDataException();
            var key = update.ClearApiKey ? null : string.IsNullOrEmpty(update.ApiKey) ? _stored!.ApiKey : update.ApiKey;
            if (!string.IsNullOrEmpty(key)) _ = DispatcharrConnection.Parse("https://validation.invalid/",key);
            var url = update.BaseUrl.Trim();
            if (url.Length > 0) url = DispatcharrConnection.Parse(url, key ?? "validation-only").BaseUri.AbsoluteUri;
            var saved = new Stored(update.Enabled,url,key,Guid.NewGuid().ToString("N"));
            var resolved = Resolve(saved);
            var directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode=FileMode.CreateNew, Access=FileAccess.Write, Share=FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var file = new FileStream(temporary,options)) file.Write(JsonSerializer.SerializeToUtf8Bytes(saved));
                File.Move(temporary,path,true);
                _stored=saved; _current=resolved;
                return View();
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

[ApiController]
[Authorize]
[Route("3picFin/Guide/Dispatcharr")]
[RequestSizeLimit(8192)]
public sealed class DispatcharrSettingsController(DispatcharrSettingsStore store, DispatcharrClient client, IUserManager users) : ControllerBase
{
    private bool Admin()
    {
        Response.Headers.CacheControl = "private, no-store";
        return User.Identity?.IsAuthenticated == true && RecentlyAddedPolicy.TryGetUserId(User,out var id) &&
            users.GetUserById(id)?.HasPermission(PermissionKind.IsAdministrator) == true;
    }
    [HttpGet]
    public IActionResult Get()
    {
        if (!Admin()) return Forbid();
        try { return Ok(store.View()); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or JsonException) { return StatusCode(503,new { Error="Category source settings are unavailable." }); }
    }
    [HttpPost]
    public IActionResult Save(DispatcharrSettingsUpdate body)
    {
        if (!Admin()) return Forbid();
        try { return Ok(store.Save(body)); }
        catch (InvalidDataException) { return BadRequest(new { Error="Use a trusted HTTP(S) management URL and a key. Blank keeps the saved key; disable before clearing it." }); }
        catch (InvalidOperationException) { return Conflict(new { Error="Settings changed. Reload before saving." }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return StatusCode(503,new { Error="Category source settings could not be saved." }); }
    }
    [HttpGet("Test")]
    public async Task<IActionResult> Test(CancellationToken token)
    {
        if (!Admin()) return Forbid();
        try
        {
            var settings=store.Snapshot(); var metadata=await client.Read(settings,token).ConfigureAwait(false);
            if (!Admin() || !store.IsCurrent(settings)) return Conflict(new { Error="Settings or permissions changed. Retry." });
            return Ok(new { Ready=true, Channels=metadata.Channels.Count, Groups=metadata.Groups.Count });
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or JsonException) { return StatusCode(503,new { Error="Metadata source unavailable. Check saved management URL, API key and source support; retry shortly." }); }
    }
}
