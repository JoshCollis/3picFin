using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Web;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Web;

public sealed class HeroAssetsTests
{
    [Fact]
    public void RowsOnlyAssetsRequireExplicitHomeRowsFlag()
    {
        var enabled = new DiscoveryPageController(() => false, () => false, () => true, () => false, () => true);
        foreach (var asset in new[] { "home-adapter.js", "home-tab-host.js", "native-home-rows.js", "native-home-rows.css" })
            Assert.IsType<FileStreamResult>(enabled.GetAsset(asset)).FileStream.Dispose();
        Assert.IsType<NotFoundResult>(enabled.GetAsset("discovery.js"));
        Assert.IsType<NotFoundResult>(enabled.GetAsset("static-hero.js"));
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => false, () => false, () => true, () => false)
            .GetAsset("home-tab-host.js"));
    }
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void HomeAssetsFollowDiscoveryOrTrustedSelectedHero(bool discovery, bool hero, bool available)
    {
        foreach (var asset in new[] { "home-adapter.js", "home-tab-host.js", "home-tab-host.css", "static-hero.js", "static-hero.css" })
        {
            var result = new DiscoveryPageController(() => discovery, () => false, () => true, () => hero).GetAsset(asset);
            if (asset.StartsWith("static-hero", StringComparison.Ordinal) ? hero : available)
                Assert.IsType<FileStreamResult>(result).FileStream.Dispose();
            else Assert.IsType<NotFoundResult>(result);
        }
        foreach (var asset in new[] { "discovery.html", "discovery.js", "discovery.css" })
        {
            var result = new DiscoveryPageController(() => discovery, () => false, () => true, () => hero).GetAsset(asset);
            if (discovery) Assert.IsType<FileStreamResult>(result).FileStream.Dispose();
            else Assert.IsType<NotFoundResult>(result);
        }
    }

    [Theory]
    [InlineData("static-hero.js", "text/javascript")]
    [InlineData("static-hero.css", "text/css")]
    public void HeroAssetsAreEmbeddedAndFeatureGated(string asset, string mime)
    {
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => false).GetAsset(asset));
        var file = Assert.IsType<FileStreamResult>(new DiscoveryPageController(() => false, () => false, () => true, () => true).GetAsset(asset));
        Assert.Contains(mime, file.ContentType);
        using var stream = file.FileStream;
        Assert.True(stream.Length > 0);
    }
}
