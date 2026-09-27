using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Rowan.Jellyfin.Plugin.Home;

/// <summary>Revalidates access on every image request; never redirects to Jellyfin's public image route.</summary>
[ApiController]
[Route("Rowan/Home")]
[Authorize]
public sealed class HeroImageController : ControllerBase
{
    private static readonly Guid PluginId = Guid.Parse("bd36ab75-0f4a-49b6-92ef-3a93da040c7a");
    private static readonly HeroImageGate Gate = new();

    private readonly IUserManager _users;
    private readonly ILibraryManager _libraries;
    private readonly IImageProcessor _images;
    private readonly IPluginManager _plugins;
    private readonly IServerApplicationPaths? _paths;

    public HeroImageController(IUserManager users, ILibraryManager libraries, IImageProcessor images, IPluginManager plugins,
        IServerApplicationPaths? paths = null)
    {
        _users = users;
        _libraries = libraries;
        _images = images;
        _plugins = plugins;
        _paths = paths;
    }

    [HttpGet("Hero/Image/{itemId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult> GetImage(Guid itemId, [FromQuery] string? tag)
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!RecentlyAddedPolicy.TryGetUserId(User, out var userId)) return Forbid();
        var user = _users.GetUserById(userId);
        if (user is null) return Forbid();
        if (!HeroImagePolicy.ValidRequest(itemId, tag)) return NotFound();
        var config = (_plugins.GetPlugin(PluginId)?.Instance as Plugin)?.Configuration;
        if (!HeroPolicy.Enabled(config)) return NotFound();

        // Jellyfin's user-aware lookup applies IsVisibleStandalone (including parental rules).
        var item = _libraries.GetItemById<BaseItem>(itemId, user);
        if (!HeroPolicy.TrySelectedLibraries(config,
            _libraries.GetUserRootFolder().GetChildren(user, true).OfType<CollectionFolder>(), out var selected)) return NotFound();
        var visibleLibraries = selected.Select(library => library.Id).ToArray();
        if (item is null) return NotFound();
        var info = item.GetImageInfo(ImageType.Backdrop, 0);
        if (info is null || !info.IsLocalFile ||
            !HeroImagePolicy.VisibleImage(item, visibleLibraries,
                HeroPolicy.VisibleInSelectedLibrary(item, visibleLibraries, _libraries.GetItemById,
                    parent => parent.IsVisibleStandalone(user)), tag,
                _images.GetImageCacheTag(item, info))) return NotFound();
        var originalPath = info.Path;

        // Only Jellyfin's internal metadata/library tree is eligible; never read media/CIFS paths.
        if (_paths is null || string.IsNullOrEmpty(_paths.InternalMetadataPath)) return NotFound();
        var root = Path.Combine(_paths.InternalMetadataPath, "library");
        using var lease = Gate.TryEnter(userId);
        if (lease is null) return StatusCode(StatusCodes.Status429TooManyRequests);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var stream = ConfinedHeroImage.TryOpen(root, info.Path);
            if (stream is null) return NotFound();
            var bytes = await HeroImageBounds.TryReadJpeg(stream, deadline.Token).ConfigureAwait(false);
            if (bytes is null) return NotFound();
            // Recheck after I/O: an old slide/tag is not authority if access or selection changed.
            user = _users.GetUserById(userId);
            config = (_plugins.GetPlugin(PluginId)?.Instance as Plugin)?.Configuration;
            if (user is null || !HeroPolicy.Enabled(config) ||
                !HeroPolicy.TrySelectedLibraries(config,
                    _libraries.GetUserRootFolder().GetChildren(user, true).OfType<CollectionFolder>(), out selected))
                return NotFound();
            item = _libraries.GetItemById<BaseItem>(itemId, user);
            info = item?.GetImageInfo(ImageType.Backdrop, 0);
            if (item is null || info is null || !info.IsLocalFile || info.Path != originalPath ||
                !HeroImagePolicy.VisibleImage(item, selected.Select(folder => folder.Id),
                    HeroPolicy.VisibleInSelectedLibrary(item, selected.Select(folder => folder.Id),
                        _libraries.GetItemById, parent => parent.IsVisibleStandalone(user)),
                    tag, _images.GetImageCacheTag(item, info))) return NotFound();
            return File(bytes, "image/jpeg");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return NotFound();
        }
    }
}

public static class HeroImagePolicy
{
    public static bool ValidRequest(Guid itemId, string? tag) => itemId != Guid.Empty &&
        tag is { Length: > 0 and <= 64 } && tag.All(char.IsAsciiHexDigit);

    public static bool VisibleImage(BaseItem item, System.Collections.Generic.IEnumerable<Guid> visibleLibraries,
        bool visibleByPolicy, string? tag, string? currentTag) =>
        item.Id != Guid.Empty && item is MediaBrowser.Controller.Entities.Movies.Movie or MediaBrowser.Controller.Entities.TV.Series &&
        visibleByPolicy && ValidRequest(item.Id, tag) &&
        string.Equals(tag, currentTag, StringComparison.OrdinalIgnoreCase) &&
        item.GetAncestorIds().Intersect(visibleLibraries).Any();
}
