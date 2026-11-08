using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Web;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Web;

public sealed class DiscoveryPageControllerTests
{
    [Fact]
    public void PluginBrandChangesWithoutChangingIdentityOrAddingWebPageRegistration()
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        Assert.Equal("3pic Fin", plugin.Name);
        Assert.Equal(Guid.Parse("bd36ab75-0f4a-49b6-92ef-3a93da040c7a"), plugin.Id);
        Assert.Single(plugin.GetPages()); // Only the admin configuration page, not Discovery navigation.
        Assert.Equal("3pic Fin", plugin.GetPages().Single().Name);
    }

    [Fact]
    public void ConfigurationPageHasVisibleBrand()
    {
        using var reader = new StreamReader(typeof(Plugin).Assembly.GetManifestResourceStream("Rowan.Jellyfin.Plugin.Configuration.configPage.html")!);
        var page = reader.ReadToEnd();
        Assert.Contains("<title>3pic Fin</title>", page, StringComparison.Ordinal);
        Assert.Contains("<h1>3pic Fin</h1>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryPageIsEnabledByDefaultButCanBeDisabled()
    {
        Assert.True(new PluginConfiguration().DiscoveryPageEnabled);
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => false).GetAsset("discovery.html"));
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => false).GetAsset("discovery.css"));
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => false).GetAsset("discovery.js"));
    }

    [Fact]
    public void NativeRowsAssetIsIndependentlyGatedByHomeFlag()
    {
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => true, () => true, () => false).GetAsset("native-home-rows.js"));
        var result = Assert.IsType<FileStreamResult>(new DiscoveryPageController(() => false, () => false, () => true).GetAsset("native-home-rows.js"));
        Assert.Equal("text/javascript; charset=utf-8", result.ContentType);
        result.FileStream.Dispose();
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => true, () => true, () => false).GetAsset("native-home-rows.css"));
        var style = Assert.IsType<FileStreamResult>(new DiscoveryPageController(() => false, () => false, () => true).GetAsset("native-home-rows.css"));
        Assert.Equal("text/css; charset=utf-8", style.ContentType);
        using var reader = new StreamReader(style.FileStream);
        Assert.Contains(".rowan-native-row__card .cardContent img", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void SearchAddonHasIndependentDefaultOffAssetGate()
    {
        Assert.False(new PluginConfiguration().GlobalSearchEnabled);
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => true, () => false).GetAsset("global-search-addon.js"));
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => true, () => false).GetAsset("global-search-addon.css"));
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => true, () => false).GetAsset("channel-search.js"));
        foreach (var (asset, mime) in new[] { ("channel-search.js", "text/javascript; charset=utf-8"), ("global-search-addon.js", "text/javascript; charset=utf-8"), ("global-search-addon.css", "text/css; charset=utf-8") })
        {
            var result = Assert.IsType<FileStreamResult>(new DiscoveryPageController(() => false, () => true).GetAsset(asset));
            Assert.Equal(mime, result.ContentType);
            result.FileStream.Dispose();
        }
    }

    [Theory]
    [InlineData("discovery.html", "text/html; charset=utf-8")]
    [InlineData("discovery.css", "text/css; charset=utf-8")]
    [InlineData("discovery.js", "text/javascript; charset=utf-8")]
    [InlineData("continuous-rail.js", "text/javascript; charset=utf-8")]
    [InlineData("home-tab-host.js", "text/javascript; charset=utf-8")]
    [InlineData("home-tab-host.css", "text/css; charset=utf-8")]
    public void EnabledPageServesEmbeddedAssetsWithExactContentType(string path, string contentType)
    {
        var controller = new DiscoveryPageController(() => true);
        var result = Assert.IsType<FileStreamResult>(controller.GetAsset(path));
        Assert.Equal(contentType, result.ContentType);
        using var reader = new StreamReader(result.FileStream);
        Assert.NotEmpty(reader.ReadToEnd());
        Assert.Equal("3picFin/Web/{asset}", typeof(DiscoveryPageController).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single().Template);
        Assert.Empty(typeof(DiscoveryPageController).GetCustomAttributes(typeof(AuthorizeAttribute), true));
    }

    [Theory]
    [InlineData("../discovery.html")]
    [InlineData("unknown.js")]
    [InlineData("Discovery.html")]
    public void UnknownAssetsAreNotServed(string path) =>
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => true).GetAsset(path));

    [Fact]
    public void ContinuousRailHasIndependentSearchGateAndNoResultPagerMarkup()
    {
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => false).GetAsset("continuous-rail.js"));
        using var script = Assert.IsType<FileStreamResult>(new DiscoveryPageController(() => false, () => true).GetAsset("continuous-rail.js")).FileStream;
        using var reader = new StreamReader(Assert.IsType<FileStreamResult>(new DiscoveryPageController(() => true).GetAsset("discovery.html")).FileStream);
        var html = reader.ReadToEnd();
        Assert.DoesNotContain("__pager", html, StringComparison.Ordinal);
        foreach (var name in new[] { "search", "requests", "shared-requests", "trending", "movies", "tv", "upcoming-movies", "upcoming-tv" })
            foreach (var suffix in new[] { "prev", "next", "page", "page-size" })
                Assert.DoesNotContain($"id=\"threepic-fin-{name}-{suffix}\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SharedRailScriptLoadsExactlyOnceBeforeItsConsumers(bool home, bool search)
    {
        var input = new Newtonsoft.Json.Linq.JObject { ["contents"] = "<html><body></body></html>" };
        var html = HomeAdapterRegistration.TransformIndex(input, enabled: home, searchEnabled: search);
        Assert.Equal(1, html.Split("data-threepic-fin-scroll src=", StringSplitOptions.None).Length - 1);
        var helper = html.IndexOf("continuous-rail.js", StringComparison.Ordinal);
        if (home) Assert.True(helper < html.IndexOf("home-adapter.js", StringComparison.Ordinal));
        if (search) Assert.True(helper < html.IndexOf("global-search-addon.js", StringComparison.Ordinal));
    }

    [Fact]
    public void PageIsInertAndContainsNoSecretBearingInlineConfiguration()
    {
        var controller = new DiscoveryPageController(() => true);
        using var reader = new StreamReader(Assert.IsType<FileStreamResult>(controller.GetAsset("discovery.html")).FileStream);
        var page = reader.ReadToEnd();
        Assert.Contains("role=\"region\" aria-label=\"3pic Fin\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("threepic-fin-discovery-title", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<main", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiClient", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apikey", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Seerr", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/3picFin/Discovery", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmbeddedFragmentDoesNotOwnAssetLinksOrRootAbsoluteUrls()
    {
        using var reader = new StreamReader(Assert.IsType<FileStreamResult>(
            new DiscoveryPageController(() => true).GetAsset("discovery.html")).FileStream);
        var fragment = reader.ReadToEnd();
        Assert.DoesNotContain("<link", fragment, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", fragment, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch("(?i)(?:href|src)\\s*=\\s*['\"]/(?!/)", fragment);
    }
}
