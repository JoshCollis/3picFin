using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;

namespace Rowan.Jellyfin.Plugin.Web;

/// <summary>Independent optional File Transformation registration; never edits the web root.</summary>
public sealed class GuidePerformanceRegistration : IHostedService
{
    private static readonly Guid RegistrationId = Guid.Parse("adabbe2e-c74b-4b5a-99aa-0ca9e5c67e21");
    private static readonly Guid PreloadRegistrationId = Guid.Parse("26c91eb7-f661-40d8-ae8a-ae5cdd98436f");
    private readonly object _lifecycle = new();
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private Task? _shutdown;
    private MethodInfo? _remove;
    private bool _registered;
    private bool _preloadRegistered;

    public GuidePerformanceRegistration() : this(null, null) { }

    public GuidePerformanceRegistration(string? serverVersion, string? webPath = null)
        => GuideModulePreloadTransform.ConfigureWebPath(webPath);

    // Compatibility is decided from native module capabilities, not this label.
    public static bool SupportsServerVersion(string? version) => true;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycle)
        {
            if (_stop is not null || _shutdown is not null) return Task.CompletedTask;
            _stop = new CancellationTokenSource();
            _worker = RegisterWhenAvailable(_stop.Token);
            return Task.CompletedTask;
        }
    }

    private async Task RegisterWhenAvailable(CancellationToken token)
    {
        while (!token.IsCancellationRequested && (!_registered || !_preloadRegistered))
        {
            try
            {
                var assembly = AssemblyLoadContext.All.SelectMany(context => context.Assemblies)
                    .FirstOrDefault(item => item.GetName().Name == "Jellyfin.Plugin.FileTransformation");
                var api = assembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
                var register = api?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static, [typeof(JObject)]);
                if (register is not null && GuidePerformanceTransform.Enabled(Plugin.Current?.Configuration))
                {
                    _remove = api!.GetMethod("RemoveTransformation", BindingFlags.Public | BindingFlags.Static);
                    if (!_registered) register.Invoke(null, [new JObject {
                        ["id"] = RegistrationId.ToString(),
                        ["fileNamePattern"] = GuidePerformanceTransform.FileNamePattern,
                        ["callbackAssembly"] = typeof(GuidePerformanceTransform).Assembly.FullName,
                        ["callbackClass"] = typeof(GuidePerformanceTransform).FullName,
                        ["callbackMethod"] = nameof(GuidePerformanceTransform.Transform)
                    }]);
                    _registered = true;
                    if (!_preloadRegistered) register.Invoke(null, [new JObject {
                        ["id"] = PreloadRegistrationId.ToString(), ["fileNamePattern"] = GuideModulePreloadTransform.IndexFileNamePattern,
                        ["callbackAssembly"] = typeof(GuideModulePreloadTransform).Assembly.FullName,
                        ["callbackClass"] = typeof(GuideModulePreloadTransform).FullName,
                        ["callbackMethod"] = nameof(GuideModulePreloadTransform.Transform)
                    }]);
                    _preloadRegistered = true;
                    break;
                }
            }
            catch (Exception) { /* Optional dependency may still be loading. Retry without blocking startup. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycle) { return _shutdown ??= StopCoreAsync(); }
    }

    private async Task StopCoreAsync()
    {
        _stop?.Cancel();
        try { if (_worker is not null) await _worker.ConfigureAwait(false); }
        finally
        {
            if (_registered)
            {
                try { _remove?.Invoke(null, [RegistrationId]); }
                catch (Exception) { /* Stopping an optional dependency cannot prevent host shutdown. */ }
                _registered = false;
            }
            if (_preloadRegistered)
            {
                try { _remove?.Invoke(null, [PreloadRegistrationId]); }
                catch (Exception) { /* Optional dependency shutdown. */ }
                _preloadRegistered = false;
            }
            _stop?.Dispose();
        }
    }
}

public sealed class GuidePerformanceServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
        => services.AddHostedService(provider => new GuidePerformanceRegistration(host.ApplicationVersionString,
            provider.GetRequiredService<IServerApplicationPaths>().WebPath));
}
