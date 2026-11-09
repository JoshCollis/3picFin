using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Rowan.Jellyfin.Plugin.Configuration;

namespace Rowan.Jellyfin.Plugin.Web;

/// <summary>A deliberately small, opt-in seam in the verified native guide, not a replacement renderer.</summary>
public static class GuidePerformanceTransform
{
    // Baseline provenance, not an OS, UA, distribution-comment or server-version
    // allowlist. The module identity and every unique capability seam gate edits.
    public const string FileNamePattern = @"^14553\.[a-f0-9]{8,64}\.chunk\.js$";
    public const string NativeSha256 = "a613661f2a2bd541c694184c769ab168d7678ee677efcdae5e0eabf8d3acfe0d";
    private const string NativeWindow = "var h=500;H=h,O(),v.StartIndex=E,v.Limit=h";
    private const string BoundedWindow = "var h=finGuide.windowSize();H=h,finGuide.loading(O),v.StartIndex=E,v.Limit=h";
    private const string NativeSettings = "t.default.show(e.categoryOptions).then((function(){e.refresh()}))";
    private const string ResetSettings = "t.default.show(e.categoryOptions).then((function(){E=0,finGuide.reset(),e.refresh()}))";
    private const string NativePrograms = "m.getLiveTvPrograms(p).then((function(t){";
    private const string ScopedPrograms = "(e.Items.length?finGuide.read(m,\"getLiveTvPrograms\",p,finTicket):Promise.resolve({Items:[]})).then((function(t){if(!finGuide.valid(finTicket))return;";
    private const string NativeFooter = """<div class="guideOptions hide"> <button is="paper-icon-button-light" type="button" class="btnPreviousPage" title="${Previous}"> <span class="material-icons arrow_back" aria-hidden="true"></span> </button> <button is="paper-icon-button-light" type="button" class="btnNextPage" title="${Next}"> <span class="material-icons arrow_forward" aria-hidden="true"></span> </button> </div> """;
    private const string NativePagingVisibility = """e.TotalRecordCount>h?(r.querySelector(".guideOptions").classList.remove("hide"),t.classList.remove("hide"),o.classList.remove("hide"),v.StartIndex?r.querySelector(".btnPreviousPage").disabled=!1:r.querySelector(".btnPreviousPage").disabled=!0,v.StartIndex+h<e.TotalRecordCount?o.disabled=!1:o.disabled=!0):r.querySelector(".guideOptions").classList.add("hide")""";
    private const string Navigation = """<div class="finGuideToolbar"> <form class="finGuideSearch focuscontainer-x"> <input type="search" class="finGuideSearchInput" placeholder="Search channels" maxlength="100" autocomplete="off" aria-label="Find channels by name or number"> <button type="submit" class="finGuideFind" aria-label="Search channels" title="Search channels"><span class="material-icons search" aria-hidden="true"></span></button> <button type="button" class="finGuideClear" aria-label="Clear channel search" title="Clear channel search"><span class="material-icons close" aria-hidden="true"></span></button> </form> <label class="finGuideCategoryLabel"><span class="finGuideCategoryCaption">Category</span><select class="finGuideCategory" aria-label="Channel category"><option value="">All categories</option></select><span class="finGuideCategoryIcon material-icons expand_more" aria-hidden="true"></span></label> </div> """;
    private const string Metadata = """<div class="finGuideMeta"><span class="finGuideRange" aria-live="polite"></span><span class="finGuideState" role="status"></span><button type="button" class="finGuideRetry" hidden>Retry guide</button><span class="finGuideCategoryState" role="status"></span><button type="button" class="finGuideCategoryRetry" hidden>Retry categories</button></div>""";

    public static bool Enabled(PluginConfiguration? config) => config?.NativeGuidePagingEnabled == true;

    public static string Transform(JObject input) => TransformContents((string?)input["contents"] ?? string.Empty,
        Enabled(Plugin.Current?.Configuration));

    public static string TransformContents(string contents, bool enabled)
    {
        if (!enabled || contents.Split(".push([[14553],{36934:function(e,t,r){", StringSplitOptions.None).Length != 2)
            return contents;
        static string? Resource(string name)
        {
            using var stream = typeof(GuidePerformanceTransform).Assembly.GetManifestResourceStream("Rowan.Jellyfin.Plugin.Web." + name);
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        var controls = Resource("native-guide-controls.js");
        var channels = Resource("channel-search.js");
        var css = Resource("native-guide-controls.css");
        if (controls is null || channels is null || css is null) return contents;
        var toolbar = ("<style>" + css + "</style>" + Navigation).Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        var changes = new (string Before, string After)[] {
            ("var L=!1;", channels + "\n" + controls + "\nvar L=!1;"),
            (NativeWindow, BoundedWindow), (NativeSettings, ResetSettings), (NativePrograms, ScopedPrograms),
            ("var h,w,D,L,x=864e5,E=0,H=0;", "var h,w,D,L,x=864e5,E=0,H=0;var finGuide=finCreateGuideController({element:e.element,limit:50,getApi:function(){return o.tF.getApiClient(e.serverId)},jump:function(start){E=start;V(e.element);N()},onError:function(){g.Ay.hide()}});"),
            ("var m=o.tF.getApiClient(e.serverId),v={StartIndex:0", "var finTicket=finGuide.begin(),m=o.tF.getApiClient(e.serverId),v={StartIndex:0"),
            ("m.getLiveTvChannels(v).then", "finGuide.read(m,\"getLiveTvChannels\",v,finTicket).then"),
            ("var t=r.querySelector(\".btnPreviousPage\"),o=r.querySelector(\".btnNextPage\");", "finGuide.update(e.TotalRecordCount,v.StartIndex,e.Items.length);"),
            ("function V(t){O(),o.tF.getApiClient(e.serverId).getLiveTvGuideInfo().then", "function V(t){var finInfo=finGuide.begin();finGuide.loading(O),finGuide.read(o.tF.getApiClient(e.serverId),\"getLiveTvGuideInfo\",void 0,finInfo).then"),
            ("t.pause=function(){M()}", "t.pause=function(){finGuide.pause(),M()}"),
            ("t.resume=function(e){e?t.refresh():N()}", "t.resume=function(e){e||finGuide.needsRefresh()?t.refresh():N()}"),
            ("t.destroy=function(){M(),", "t.destroy=function(){finGuide.pause(),M(),"),
            (NativeFooter, ""), (NativePagingVisibility, "void 0"),
            ("X.querySelector(\".btnNextPage\").addEventListener(\"click\",(function(){E+=H,V(X),N()})),X.querySelector(\".btnPreviousPage\").addEventListener(\"click\",(function(){E=Math.max(E-H,0),V(X),N()})),", ""),
            ("data-skipfocuswhenvisible=\"true\" data-horizontal=\"false\"", "data-skipfocuswhenvisible=\"true\" data-horizontal=\"false\" data-scrollevent=\"true\""),
            ("!c&&l.Name&&(o+='<div class=\"guideChannelName\">'", "l.Name&&(o+='<div class=\"guideChannelName\">'"),
            ("(r,M,e.Items,t.Items,R,o,m),g.Ay.hide()", "(r,M,e.Items,t.Items,R,o,m),finGuide.rendered(finTicket,e.Items,t.Items,M),g.Ay.hide()"),
            ("<div class=\"tvGuideHeader\">", toolbar + "<div class=\"tvGuideHeader\">"),
            ("<div class=\"guideHeaderDateSelection\">", "<div class=\"guideHeaderDateSelection\">" + Metadata),
            ("z(e,l,g,60*(60*m+f)*1e3,c,b.A.tv)", "z(e,l,g,60*(60*m+f)*1e3,c,finGuide.focusProgramOnRender(b.A.tv))"),
            ("var Q=X.querySelector(\".programGrid\"),Y=X.querySelector(\".timeslotHeaders\");", "var Q=X.querySelector(\".programGrid\"),Y=X.querySelector(\".timeslotHeaders\");finGuide.mount();")
        };
        foreach (var (before, _) in changes)
            if (contents.Split(before, StringSplitOptions.None).Length != 2) return contents;
        // Reuse the native renderer with a bounded, overscanned channel window.
        // Remove native result-page markup AND listeners, not just visibility.
        // Matching column padding retains catalogue scroll geometry. Native item
        // maps, programme actions, 24-hour dates and permissions remain native.
        // Never submit channelIds='' (the API interprets that as all channels).
        foreach (var (before, after) in changes) contents = contents.Replace(before, after, StringComparison.Ordinal);
        return contents;
    }
}
