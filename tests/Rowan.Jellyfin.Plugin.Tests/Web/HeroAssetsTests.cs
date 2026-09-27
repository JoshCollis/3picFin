using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Web;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Web;

public sealed class HeroAssetsTests
{
    [Theory]
    [InlineData("static-hero.js", "text/javascript")]
    [InlineData("static-hero.css", "text/css")]
    public void HeroAssetsAreEmbeddedAndFeatureGated(string asset, string mime)
    {
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => false).GetAsset(asset));
        var file = Assert.IsType<FileStreamResult>(new DiscoveryPageController(() => true).GetAsset(asset));
        Assert.Contains(mime, file.ContentType);
        using var stream = file.FileStream;
        Assert.True(stream.Length > 0);
    }
}
