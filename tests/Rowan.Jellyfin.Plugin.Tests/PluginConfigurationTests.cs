using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using Rowan.Jellyfin.Plugin;
using Rowan.Jellyfin.Plugin.Configuration;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests;

public sealed class PluginConfigurationTests
{
    [Fact]
    public void NewInstallEnablesHomeAndDiscoveryButNotTrustedImages()
    {
        var configuration = new PluginConfiguration();

        Assert.True(configuration.HomeEnabled);
        Assert.True(configuration.DiscoveryPageEnabled);
        Assert.False(configuration.HeroTrustedFilesystemEnabled);
    }

    [Fact]
    public void HeroRequiresBothHomeAndTrustedFilesystemOptIn()
    {
        Assert.False(Rowan.Jellyfin.Plugin.Home.HeroPolicy.Enabled(null));
        Assert.False(Rowan.Jellyfin.Plugin.Home.HeroPolicy.Enabled(new PluginConfiguration()));
        Assert.False(Rowan.Jellyfin.Plugin.Home.HeroPolicy.Enabled(new PluginConfiguration { HomeEnabled = true }));
        Assert.False(Rowan.Jellyfin.Plugin.Home.HeroPolicy.Enabled(new PluginConfiguration { HomeEnabled = false, HeroTrustedFilesystemEnabled = true }));
        var enabled = new PluginConfiguration { HomeEnabled = true, HeroTrustedFilesystemEnabled = true };
        Assert.True(Rowan.Jellyfin.Plugin.Home.HeroPolicy.Enabled(enabled));
        Assert.True(JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(enabled))!.HeroTrustedFilesystemEnabled);
    }

    [Fact]
    public void UnsetLibraryFilterMeansAllEligibleLibraries()
    {
        var configuration = new PluginConfiguration();

        Assert.Null(configuration.RecentlyAddedLibraryIds);
    }

    [Fact]
    public void ExplicitEmptyLibraryFilterSurvivesSerialization()
    {
        var configuration = new PluginConfiguration { RecentlyAddedLibraryIds = [] };

        var restored = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(configuration));

        Assert.NotNull(restored);
        Assert.NotNull(restored.RecentlyAddedLibraryIds);
        Assert.Empty(restored.RecentlyAddedLibraryIds);
    }

    [Fact]
    public void PluginPageIsEmbeddedAndNotEmpty()
    {
        var assembly = typeof(Plugin).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(".Configuration.configPage.html", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource);
        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void PluginPageExposesLibraryModesAndUsesAuthenticatedVirtualFoldersEndpoint()
    {
        var assembly = typeof(Plugin).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(".Configuration.configPage.html", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        var page = reader.ReadToEnd();

        Assert.Contains("id=\"RecentlyAddedAll\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"RecentlyAddedSelected\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"RecentlyAddedNone\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"RecentlyAddedLibraries\"", page, StringComparison.Ordinal);
        Assert.Contains("ApiClient.getJSON(ApiClient.getUrl('Library/VirtualFolders'))", page, StringComparison.Ordinal);
        Assert.Contains("config.RecentlyAddedLibraryIds", page, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginPageHasSafeRenderingValidationAndVisibleErrors()
    {
        var assembly = typeof(Plugin).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(".Configuration.configPage.html", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        var page = reader.ReadToEnd();

        Assert.Contains("textContent", page, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", page, StringComparison.Ordinal);
        Assert.Contains("id=\"RowanConfigError\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"RowanConfigStatus\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"RowanSaveButton\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginPageExposesHomeToggleAndSavesConfiguration()
    {
        var assembly = typeof(Plugin).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(".Configuration.configPage.html", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        var page = reader.ReadToEnd();

        Assert.Contains("id=\"HomeEnabled\"", page, StringComparison.Ordinal);
        Assert.Contains("config.HomeEnabled", page, StringComparison.Ordinal);
        Assert.Contains("ApiClient.updatePluginConfiguration", page, StringComparison.Ordinal);
    }
}
