using System.Net.Http;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Rowan.Jellyfin.Plugin.Configuration;

namespace Rowan.Jellyfin.Plugin.Discovery;

/// <summary>Jellyfin v12.1 plugin registration. The factory owns the non-redirecting production handler.</summary>
public sealed class SeerrServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient("Rowan.Seerr")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        serviceCollection.AddSingleton<SeerrReadCache>();
        serviceCollection.AddTransient(provider => new SeerrClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("Rowan.Seerr"),
            SeerrOptions.FromConfiguration(Plugin.Current?.Configuration ?? new PluginConfiguration()),
            readCache: provider.GetRequiredService<SeerrReadCache>(),
            fourKEnabled: () => Plugin.Current?.Configuration.Enable4kRequests == true));
    }
}
