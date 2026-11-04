using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Web;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Web;

public sealed class GuidePerformanceTests
{
    private sealed class NativeGuideAssetFactAttribute : FactAttribute
    {
        public NativeGuideAssetFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FIN_GUIDE_NATIVE_ASSET")))
                Skip = "Set FIN_GUIDE_NATIVE_ASSET to the public hash-pinned guide chunk for integration verification.";
        }
    }

    private static class RemovalProbe
    {
        internal static int Count;
        public static void Remove(Guid _) => Interlocked.Increment(ref Count);
    }

    [Theory]
    [InlineData(true,false)]
    [InlineData(false,true)]
    [InlineData(true,true)]
    public async Task RegistrationShutdownJoinsWorkerAndRemovesEachCompletedRegistrationOnceEvenWithCancelledHostToken(bool native, bool preload)
    {
        RemovalProbe.Count = 0;
        var service = new GuidePerformanceRegistration();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var type = typeof(GuidePerformanceRegistration);
        type.GetField("_stop", flags)!.SetValue(service, new CancellationTokenSource());
        type.GetField("_remove", flags)!.SetValue(service, typeof(RemovalProbe).GetMethod(nameof(RemovalProbe.Remove)));
        type.GetField("_worker", flags)!.SetValue(service, Task.Run(async () => {
            await release.Task;
            type.GetField("_registered", flags)!.SetValue(service, native);
            type.GetField("_preloadRegistered", flags)!.SetValue(service, preload);
        }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stop = service.StopAsync(cancelled.Token);
        release.SetResult();
        await stop;
        await service.StopAsync(CancellationToken.None);
        Assert.Equal((native?1:0)+(preload?1:0), RemovalProbe.Count);
    }

    [Fact]
    public void RegistrationMatchesOnlyTheReviewedNativeAsset()
    {
        Assert.Equal("index.html",GuideModulePreloadTransform.IndexFileNamePattern);
        Assert.Matches(GuidePerformanceTransform.FileNamePattern, "14553.6ddb0da23f2698557d33.chunk.js");
        foreach (var other in new[] { "index.html", "main.jellyfin.bundle.js", "14553.other.chunk.js",
            "plugin-14553.6ddb0da23f2698557d33.chunk.js", "14553.6ddb0da23f2698557d33.chunk.js.bak" })
            Assert.DoesNotMatch(GuidePerformanceTransform.FileNamePattern, other);
    }

    [Theory]
    [InlineData("12.3.0", true)]
    [InlineData("12.3.1.0", true)]
    [InlineData("12.1.0", true)]
    [InlineData("12.2.0", true)]
    [InlineData("12.4.0", true)]
    [InlineData("13.0.0", true)]
    [InlineData("12.3.0-preview", true)]
    [InlineData("12.3", true)]
    [InlineData(null, true)]
    public void HostVersionDoesNotPreventInspectionOfNativeSourceCapabilities(string? version, bool expected)
        => Assert.Equal(expected, GuidePerformanceRegistration.SupportsServerVersion(version));

    [Fact]
    public void BudgetIsIndependentAndOptIn()
    {
        Assert.False(new PluginConfiguration().NativeGuidePagingEnabled);
        Assert.False(GuidePerformanceTransform.Enabled(null));
        Assert.True(GuidePerformanceTransform.Enabled(new PluginConfiguration {
            HomeEnabled = false, GlobalSearchEnabled = false, NativeGuidePagingEnabled = true
        }));
    }

    [Fact]
    public void UnknownOrAlreadyChangedAssetsAreNotRewritten()
    {
        foreach (var source in new[] { "", "var h=500;H=h,O(),v.StartIndex=E,v.Limit=h", "unrelated script" })
        {
            Assert.Equal(source, GuidePerformanceTransform.TransformContents(source, true));
            Assert.Equal(source, GuidePerformanceTransform.TransformContents(source, false));
        }
    }

    [NativeGuideAssetFact]
    public void ExactServedAssetRetainsNativeCatalogueAndTimeSemantics()
    {
        // Optional integration input: public, hash-pinned upstream code, never a secret DTO.
        var input = Environment.GetEnvironmentVariable("FIN_GUIDE_NATIVE_ASSET");
        Assert.False(string.IsNullOrEmpty(input));
        var source = File.ReadAllText(input!);
        var result = GuidePerformanceTransform.TransformContents(source, true);
        Assert.NotEqual(source, result);
        Assert.Contains("var h=50;H=h,O(),v.StartIndex=E,v.Limit=h", result);
        Assert.Contains("limit:50,getApi:", result);
        Assert.Contains("finGuideCategory", result);
        Assert.Contains("t.default.show(e.categoryOptions).then((function(){E=0,e.refresh()}))", result);
        Assert.Contains("(e.Items.length?finGuide.read(m,\"getLiveTvPrograms\",p,finTicket):Promise.resolve({Items:[]})).then", result);
        Assert.Contains("finGuideLast", result);
        Assert.Contains("finGuidePage", result);
        Assert.Contains("finGuideSearchInput", result);
        Assert.Contains("ThreePicFinChannelSearch", result);
        Assert.Contains("finGuide.pause()", result);
        Assert.Contains("if(!finGuide.valid(finTicket))return;", result);
        // The actual renderer and action markup remain native; controls/request
        // coordination must not fork its programme or channel rendering logic.
        static string Between(string text, string start, string end) => text[text.IndexOf(start, StringComparison.Ordinal)..text.IndexOf(end, StringComparison.Ordinal)];
        Assert.Equal(Between(source, "function F(", "function _("), Between(result, "function F(", "function _("));
        Assert.Equal(Between(source, "function G(", "function K("), Between(result, "function G(", "function K("));
        Assert.Equal(source, GuidePerformanceTransform.TransformContents(source, false));
        Assert.Equal(result, GuidePerformanceTransform.TransformContents(result, true));
        Assert.NotEqual(source + "\n", GuidePerformanceTransform.TransformContents(source + "\n", true));
        Assert.NotEqual(source + "\n/* distribution comment */", GuidePerformanceTransform.TransformContents(source + "\n/* distribution comment */", true));
        var unsupported = source.Replace("var h=500;H=h", "var h=501;H=h", StringComparison.Ordinal);
        Assert.Equal(unsupported, GuidePerformanceTransform.TransformContents(unsupported, true));
        Assert.Contains("E+=H,V(X),N()", result);
        Assert.Contains("channelIds:e.Items.map", result);
        Assert.Contains("finGuide.update(e.TotalRecordCount,v.StartIndex,e.Items.length)", result);
        Assert.Contains("x=864e5", result);
        var output = Environment.GetEnvironmentVariable("FIN_GUIDE_PATCHED_ASSET");
        if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, result);
    }

    [NativeGuideAssetFact]
    public void GuideOnlyVersionedPreloadPrecedesDeferredBootstrapWithoutTouchingOtherScripts()
    {
        var source = File.ReadAllText(Environment.GetEnvironmentVariable("FIN_GUIDE_NATIVE_ASSET")!);
        const string html = "<html><body><script defer src=\"runtime.bundle.js?v=1\"></script><script defer src=\"node_modules.regenerator-runtime.bundle.js?v=1\"></script><script defer src=\"main.jellyfin.bundle.js?v=1\"></script></body></html>";
        var result = GuideModulePreloadTransform.TransformIndexContents(html, true, "14553.6ddb0da23f2698557d33.chunk.js", source);
        Assert.Contains("data-threepic-fin-guide-module", result);
        Assert.Contains("14553.6ddb0da23f2698557d33.chunk.js?fin-guide=", result);
        Assert.Contains("<script defer src=\"runtime.bundle.js?v=1\"></script>", result);
        Assert.Equal(result, GuideModulePreloadTransform.TransformIndexContents(result, true, "14553.6ddb0da23f2698557d33.chunk.js", source));
        Assert.Equal(html, GuideModulePreloadTransform.TransformIndexContents(html, false, "14553.6ddb0da23f2698557d33.chunk.js", source));
        Assert.Equal(html, GuideModulePreloadTransform.TransformIndexContents(html, true, "unrelated.js", source));
        Assert.Equal(html, GuideModulePreloadTransform.TransformIndexContents(html, true, "14553.6ddb0da23f2698557d33.chunk.js", "unsupported"));
        var blocking = html.Replace("defer ", "", StringComparison.Ordinal);
        Assert.Equal(blocking, GuideModulePreloadTransform.TransformIndexContents(blocking, true, "14553.6ddb0da23f2698557d33.chunk.js", source));
        var asynchronous = html.Replace("defer ", "async defer ", StringComparison.Ordinal);
        Assert.Equal(asynchronous, GuideModulePreloadTransform.TransformIndexContents(asynchronous, true, "14553.6ddb0da23f2698557d33.chunk.js", source));
        var duplicate = source + ";var L=!1;";
        Assert.Equal(duplicate, GuidePerformanceTransform.TransformContents(duplicate,true));
        var unrelated = source.Replace(".push([[14553],{36934:",".push([[99999],{36934:",StringComparison.Ordinal);
        Assert.Equal(unrelated, GuidePerformanceTransform.TransformContents(unrelated,true));
        var index = Environment.GetEnvironmentVariable("FIN_GUIDE_NATIVE_INDEX");
        if (!string.IsNullOrEmpty(index))
        {
            var realHtml=File.ReadAllText(index);
            Assert.NotEqual(realHtml,GuideModulePreloadTransform.TransformIndexContents(realHtml,true,"14553.6ddb0da23f2698557d33.chunk.js",source));
        }
    }

    [Fact]
    public void CallbackDefaultsOffAndPreservesUnknownInput()
    {
        var input = new JObject { ["contents"] = "unchanged" };
        Assert.Equal("unchanged", GuidePerformanceTransform.Transform(input));
        Assert.Equal(string.Empty, GuidePerformanceTransform.Transform(new JObject()));
    }
}
