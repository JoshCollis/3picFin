using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Rowan.Jellyfin.Plugin.Web;

/// <summary>Version only the native guide module; no cache/storage/worker reset or network monkeypatch.</summary>
public static class GuideModulePreloadTransform
{
    // FT 3.0.3 selects one exact-key pipeline before considering regex keys.
    // Share Home's index key, otherwise its existing loader shadows this one.
    public const string IndexFileNamePattern = "index.html";
    private static string? _webPath;
    public static void ConfigureWebPath(string? path) => Volatile.Write(ref _webPath, path);

    public static string Transform(JObject input)
    {
        var html = (string?)input["contents"] ?? string.Empty;
        if (!GuidePerformanceTransform.Enabled(Plugin.Current?.Configuration)) return html;
        var root = Volatile.Read(ref _webPath);
        if (string.IsNullOrWhiteSpace(root)) return html;
        try
        {
            // A unique public module candidate is required. Never select an old
            // distribution at random, scan arbitrary directories or read user data.
            var candidates = Directory.EnumerateFiles(root, "14553.*.chunk.js", SearchOption.TopDirectoryOnly)
                .Where(path => Regex.IsMatch(Path.GetFileName(path), GuidePerformanceTransform.FileNamePattern,
                    RegexOptions.CultureInvariant)).Take(3).ToArray();
            if (candidates.Length != 1 || new FileInfo(candidates[0]).Length > 512 * 1024) return html;
            return TransformIndexContents(html, true, Path.GetFileName(candidates[0]), File.ReadAllText(candidates[0]));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return html; }
    }

    public static string TransformIndexContents(string html, bool enabled, string fileName, string nativeSource)
    {
        if (!enabled || html.Contains("data-threepic-fin-guide-module", StringComparison.Ordinal) ||
            !Regex.IsMatch(fileName, GuidePerformanceTransform.FileNamePattern, RegexOptions.CultureInvariant) ||
            html.Split("</body>", StringSplitOptions.None).Length != 2) return html;
        var scripts = Regex.Matches(html, "<script\\b[^>]*\\bsrc\\s*=\\s*[\"'](?<src>[^\"']*\\.bundle\\.js[^\"']*)[\"'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static string SourcePath(Match script) => script.Groups["src"].Value.Split('?', '#')[0];
        if (scripts.Count < 2 || scripts.Cast<Match>().Count(match => SourcePath(match) is "runtime.bundle.js" or "./runtime.bundle.js") != 1 ||
            scripts.Cast<Match>().Count(match => SourcePath(match) is "main.jellyfin.bundle.js" or "./main.jellyfin.bundle.js") != 1 ||
            scripts.Cast<Match>().Any(match => !Regex.IsMatch(match.Value, @"\bdefer(?:\s|=|>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                Regex.IsMatch(match.Value, @"\basync(?:\s|=|>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) return html;
        var transformed = GuidePerformanceTransform.TransformContents(nativeSource, true);
        if (transformed == nativeSource) return html;
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(transformed)));
        // This ordinary non-deferred chunk script queues the native module before
        // deferred app bootstrap. Webpack then uses that module instead of asking
        // the browser for a potentially old immutable, unversioned guide chunk.
        // No active instance is hot-replaced. Only a fresh document adopts it.
        return html.Replace("</body>", "<script data-threepic-fin-guide-module src=\"" + fileName +
            "?fin-guide=" + revision + "\"></script></body>", StringComparison.Ordinal);
    }
}
