using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using Rowan.Jellyfin.Plugin.Web;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Web;

public sealed class HomeAdapterRegistrationTests
{
    private static class RemovalProbe
    {
        internal static int Count;
        public static void Remove(Guid _) => Interlocked.Increment(ref Count);
    }

    [Fact]
    public async Task ShutdownCancellationCannotSkipRemovalWhileRegistrationFinishes()
    {
        RemovalProbe.Count = 0;
        var service = new HomeAdapterRegistration();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(HomeAdapterRegistration);
        type.GetField("_stop", flags)!.SetValue(service, new CancellationTokenSource());
        type.GetField("_remove", flags)!.SetValue(service, typeof(RemovalProbe).GetMethod(nameof(RemovalProbe.Remove)));
        type.GetField("_worker", flags)!.SetValue(service, Task.Run(async () => {
            started.SetResult();
            await release.Task;
            type.GetField("_registered", flags)!.SetValue(service, true);
        }));
        await started.Task;
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        var stopping = service.StopAsync(shutdown.Token);
        release.SetResult();
        await stopping;
        Assert.Equal(1, RemovalProbe.Count);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    public void IndexEligibilityAllowsIndependentHero(bool home, bool discovery, bool trustedHero, bool expected)
    {
        var config = new Rowan.Jellyfin.Plugin.Configuration.PluginConfiguration {
            HomeEnabled = home, DiscoveryPageEnabled = discovery,
            HeroTrustedFilesystemEnabled = trustedHero,
            HeroLibraryIds = [Guid.NewGuid()]
        };
        Assert.Equal(expected, HomeAdapterRegistration.ShouldInject(config));
        config.HeroLibraryIds = [];
        Assert.Equal(home && discovery, HomeAdapterRegistration.ShouldInject(config));
    }

    [Fact]
    public void UnknownIndexIsUnchanged()
    {
        const string html = "<html><body>unknown</body></html>";
        Assert.Equal(html, HomeAdapterRegistration.TransformIndex(new JObject { ["contents"] = html }, "not-a-hash"));
    }

    [Fact]
    public void ExactIndexIsInjectedOnceWithoutRemovingPriorTags()
    {
        const string html = "<html><body><script data-hss></script><script data-plugin-pages></script></body></html>";
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(html)));
        var result = HomeAdapterRegistration.TransformIndex(new JObject { ["contents"] = html }, hash);
        Assert.Contains("data-threepic-fin-adapter", result);
        Assert.Contains("data-hss", result);
        Assert.Contains("data-plugin-pages", result);
        Assert.Equal(result, HomeAdapterRegistration.TransformIndex(new JObject { ["contents"] = result }, hash));
        Assert.Equal(html, HomeAdapterRegistration.TransformIndex(new JObject { ["contents"] = html }, hash, enabled: false));
        Assert.Equal(html, HomeAdapterRegistration.TransformIndex(new JObject { ["contents"] = html }, "incorrect"));
    }

    [Fact]
    public void PortableIndexRejectsMalformedAndAmbiguousHtml()
    {
        foreach (var html in new[] { "hello</body>", "<html><body>x</body></html></body>", "<html><body>x</body>" })
            Assert.Equal(html, HomeAdapterRegistration.TransformIndex(new JObject { ["contents"] = html }));
    }

    [Fact]
    public void PortableIndexInjectsWithoutDistributionHash()
    {
        const string html = "<!doctype html><html><head></head><body><script data-hss></script><script data-plugin-pages></script></body></html>";
        var output = HomeAdapterRegistration.TransformIndex(new JObject { ["contents"] = html });
        Assert.Equal(1, output.Split("data-threepic-fin-adapter").Length - 1);
        Assert.Equal(1, output.Split("data-threepic-fin-global-nav src=").Length - 1);
        Assert.Contains("global-fin-nav.css", output);
        Assert.IsType<FileStreamResult>(new DiscoveryPageController(() => true, () => false, () => true).GetAsset("global-fin-nav.js"));
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => false).GetAsset("global-fin-nav.js"));
        Assert.Contains("data-hss", output);
        Assert.Contains("data-plugin-pages", output);
        Assert.Equal(output, HomeAdapterRegistration.TransformIndex(new JObject { ["contents"] = output }));
    }

    [Fact]
    public void SearchOnlyIndexInjectsPortableLoaderAndKeepsPriorTags()
    {
        const string html = "<html><body><script data-hss></script><script data-plugin-pages></script></body></html>";
        var output = HomeAdapterRegistration.TransformIndex(new JObject { ["contents"] = html }, enabled: false, searchEnabled: true);
        Assert.DoesNotContain("data-threepic-fin-adapter", output);
        Assert.Equal(1, output.Split("data-threepic-fin-search src=").Length - 1);
        Assert.Contains("search-adapter.js", output);
        Assert.Contains("data-hss", output);
        Assert.Contains("data-plugin-pages", output);
        Assert.Equal(output, HomeAdapterRegistration.TransformIndex(new JObject { ["contents"] = output }, enabled: false, searchEnabled: true));
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => false, () => false).GetAsset("search-adapter.js"));
        Assert.IsType<FileStreamResult>(new DiscoveryPageController(() => false, () => true).GetAsset("search-adapter.js"));
    }

    [Fact]
    public void PackagedAdapterIsFlagGated()
    {
        Assert.IsType<NotFoundResult>(new DiscoveryPageController(() => false).GetAsset("home-adapter.js"));
        var result = Assert.IsType<FileStreamResult>(new DiscoveryPageController(() => true).GetAsset("home-adapter.js"));
        Assert.Equal("text/javascript; charset=utf-8", result.ContentType);
        using var reader = new StreamReader(result.FileStream);
        Assert.Contains("ThreePicFinHomeHost", reader.ReadToEnd());
    }
}
