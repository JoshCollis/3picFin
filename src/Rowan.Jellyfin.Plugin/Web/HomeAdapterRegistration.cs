using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;

namespace Rowan.Jellyfin.Plugin.Web;

/// <summary>Optional exact-index registration through File Transformation's published reflection interface.</summary>
public sealed class HomeAdapterRegistration : IHostedService
{
    private static readonly Guid RegistrationId = Guid.Parse("a5842b46-e6f9-4aaf-91dd-9ce876453ecd");
    // No production web distribution has passed the exact-response and signed-in gates.
    // A disposable test build may replace this placeholder; never promote a lab pin to production.
    private static readonly string? VerifiedIndexSha256 = null;
    private readonly object _lifecycle = new();
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private Task? _shutdown;
    private MethodInfo? _remove;
    private bool _registered;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycle)
        {
            if (VerifiedIndexSha256 is null || _stop is not null || _shutdown is not null) return Task.CompletedTask;
            _stop = new CancellationTokenSource();
            _worker = RegisterWhenAvailable(_stop.Token);
            return Task.CompletedTask;
        }
    }

    private async Task RegisterWhenAvailable(CancellationToken token)
    {
        while (!token.IsCancellationRequested && !_registered)
        {
            try
            {
                var assembly = AssemblyLoadContext.All.SelectMany(context => context.Assemblies)
                    .FirstOrDefault(item => item.GetName().Name == "Jellyfin.Plugin.FileTransformation");
                var api = assembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
                var register = api?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static, [typeof(JObject)]);
                if (register is not null && Plugin.Current?.Configuration.HomeEnabled == true &&
                    Plugin.Current.Configuration.DiscoveryPageEnabled)
                {
                    var payload = new JObject
                    {
                        ["id"] = RegistrationId.ToString(),
                        ["fileNamePattern"] = "index.html",
                        ["callbackAssembly"] = typeof(HomeAdapterRegistration).Assembly.FullName,
                        ["callbackClass"] = typeof(HomeAdapterRegistration).FullName,
                        ["callbackMethod"] = nameof(Transform)
                    };
                    register.Invoke(null, [payload]);
                    _remove = api!.GetMethod("RemoveTransformation", BindingFlags.Public | BindingFlags.Static);
                    _registered = true;
                    break;
                }
            }
            catch (Exception) { /* Optional dependency may be loading or unavailable. Retry without blocking startup. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycle)
        {
            // The host's shutdown token may already be cancelled. Cleanup must still
            // join the registration worker before inspecting its registration state.
            return _shutdown ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        _stop?.Cancel();
        try
        {
            if (_worker is not null) await _worker.ConfigureAwait(false);
        }
        finally
        {
            if (_registered)
            {
                try { _remove?.Invoke(null, [RegistrationId]); }
                catch (Exception) { /* Stopping the optional dependency must not prevent server shutdown. */ }
                _registered = false;
            }
            _stop?.Dispose();
        }
    }

    public static string Transform(JObject input) => TransformIndex(input, VerifiedIndexSha256,
        Plugin.Current?.Configuration.HomeEnabled == true && Plugin.Current.Configuration.DiscoveryPageEnabled);

    public static string TransformIndex(JObject input, string? expectedSha256, bool enabled = true)
    {
        var html = (string?)input["contents"] ?? string.Empty;
        if (!enabled || string.IsNullOrEmpty(expectedSha256)) return html;
        if (html.Contains("data-threepic-fin-adapter", StringComparison.Ordinal)) return html;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(html)));
        if (!string.Equals(hash, expectedSha256, StringComparison.Ordinal)) return html;
        const string anchor = "</body>";
        if (html.Split(anchor, StringSplitOptions.None).Length != 2) return html;
        return html.Replace(anchor, "<script data-threepic-fin-adapter src=\"../3picFin/Web/home-adapter.js\"></script>" + anchor, StringComparison.Ordinal);
    }
}

public sealed class HomeAdapterServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
        => services.AddHostedService<HomeAdapterRegistration>();
}
