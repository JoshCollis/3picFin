using System;
using System.Text.Json;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Discovery;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Discovery;

public sealed class SeerrOptionsTests
{
    [Fact]
    public void ModuleAndCredentialsAreAbsentByDefault()
    {
        var configuration = new PluginConfiguration();
        Assert.False(configuration.SeerrEnabled);
        Assert.Null(configuration.SeerrBaseUrl);
        Assert.Null(configuration.SeerrApiKey);
        Assert.Null(SeerrOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void DisabledModuleDoesNotUseSavedCredentials()
    {
        var configuration = Enabled("https://seerr.example", "example-secret");
        configuration.SeerrEnabled = false;
        Assert.Null(SeerrOptions.FromConfiguration(configuration));
    }

    [Theory]
    [InlineData(null, "example-secret")]
    [InlineData("", "example-secret")]
    [InlineData("  ", "example-secret")]
    [InlineData("https://seerr.example", null)]
    [InlineData("https://seerr.example", "")]
    [InlineData("https://seerr.example", "  ")]
    public void MissingCredentialsDisableUpstreamCalls(string? url, string? key)
    {
        Assert.Null(SeerrOptions.FromConfiguration(Enabled(url, key)));
    }

    [Theory]
    [InlineData("http://seerr.example:5055/")]
    [InlineData("https://seerr.example/seerr/")]
    public void ValidAbsoluteHttpUrlsProduceEffectiveOptions(string url)
    {
        var options = SeerrOptions.FromConfiguration(Enabled(url, "example-secret"));
        Assert.NotNull(options);
        Assert.Equal(new Uri(url), options.BaseUri);
        Assert.Equal("example-secret", options.ApiKey);
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("ftp://seerr.example")]
    [InlineData("not a url")]
    [InlineData("https://user:pass@seerr.example")]
    [InlineData("https://seerr.example/#fragment")]
    [InlineData("https://seerr.example/?token=secret")]
    [InlineData("https://seerr.example/?")]
    [InlineData("https://seerr.example/#")]
    [InlineData("https://@seerr.example/")]
    public void InvalidBaseUrlsFailClosedWithoutEchoingConfiguration(string url)
    {
        var exception = Assert.Throws<ArgumentException>(() => SeerrOptions.FromConfiguration(Enabled(url, "example-secret")));
        Assert.DoesNotContain("example-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(url, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SavedCredentialsRoundTripButDiagnosticsDoNotContainKey()
    {
        var original = Enabled("https://seerr.example", "example-secret");
        var restored = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(original));
        Assert.NotNull(restored);
        Assert.True(restored.SeerrEnabled);
        Assert.Equal(original.SeerrBaseUrl, restored.SeerrBaseUrl);
        Assert.Equal(original.SeerrApiKey, restored.SeerrApiKey);
        var options = SeerrOptions.FromConfiguration(restored);
        Assert.NotNull(options);
        Assert.DoesNotContain("example-secret", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveOptionsJsonDoesNotExposeApiKey()
    {
        var options = SeerrOptions.FromConfiguration(Enabled("https://seerr.example/seerr", "example-secret"));
        Assert.NotNull(options);

        var json = JsonSerializer.Serialize(options);

        Assert.DoesNotContain("example-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://seerr.example/seerr")]
    [InlineData("https://seerr.example/seerr/")]
    public void RelativeApiPathsRemainUnderConfiguredBasePath(string url)
    {
        var options = SeerrOptions.FromConfiguration(Enabled(url, "example-secret"));
        Assert.NotNull(options);

        Assert.Equal(new Uri("https://seerr.example/seerr/"), options.BaseUri);
        Assert.Equal(new Uri("https://seerr.example/seerr/api/v1/status"), new Uri(options.BaseUri, "api/v1/status"));
    }

    [Fact]
    public void DiagnosticsDoNotIncludeUrlPathOrKey()
    {
        var options = SeerrOptions.FromConfiguration(Enabled("https://seerr.example/private-path", "example-secret"));
        Assert.NotNull(options);
        Assert.DoesNotContain("private-path", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("example-secret", options.ToString(), StringComparison.Ordinal);
    }

    private static PluginConfiguration Enabled(string? url, string? key) => new()
    {
        SeerrEnabled = true,
        SeerrBaseUrl = url,
        SeerrApiKey = key
    };
}
