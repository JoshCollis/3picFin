using System;
using System.IO;
using System.Linq;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Web;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Web;

public sealed class FeatureCompositionTests
{
    [Fact]
    public void CatalogScrollingDoesNotRemoveCurrentGuideAndChannelSearchAssets()
    {
        var assembly = typeof(GuidePerformanceTransform).Assembly;
        foreach (var file in new[] { "continuous-rail.js", "channel-search.js", "native-guide-controls.js",
            "native-guide-controls.css", "home-adapter.js", "home-tab-host.js", "global-fin-nav.js", "search-adapter.js" })
        {
            using var resource = assembly.GetManifestResourceStream("Rowan.Jellyfin.Plugin.Web." + file);
            Assert.NotNull(resource);
            Assert.True(resource.Length > 0);
        }
        Assert.False(new PluginConfiguration().NativeGuidePagingEnabled);
        Assert.NotNull(typeof(GuideCategoriesController));
        Assert.NotNull(typeof(GuidePerformanceRegistration));
        Assert.NotNull(typeof(GuideModulePreloadTransform));
        using var reader = new StreamReader(assembly.GetManifestResourceStream("Rowan.Jellyfin.Plugin.Configuration.configPage.html")!);
        Assert.Contains("NativeGuidePagingEnabled", reader.ReadToEnd(), StringComparison.Ordinal);
    }
}
