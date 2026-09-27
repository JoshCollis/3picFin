using System.Net.Http;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Rowan.Jellyfin.Plugin.Configuration;

namespace Rowan.Jellyfin.Plugin.Downloads;

/// <summary>Jellyfin v12.1 registrator; redirects must not forward upstream API keys.</summary>
public sealed class DownloadsServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddHttpClient("Rowan.ArrDownloads")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<DownloadsAdmission>();
        services.AddTransient(provider => new ArrDownloadsClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("Rowan.ArrDownloads"),
            ArrDownloadsOptions.TryFromConfiguration(Plugin.Current?.Configuration ?? new PluginConfiguration()),
            provider.GetRequiredService<DownloadsAdmission>()));
    }
}
