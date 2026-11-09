using System;
using System.IO;
using System.Net.Http;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Rowan.Jellyfin.Plugin.Web;

public sealed class DispatcharrServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddHttpClient("3picFin.Dispatcharr", c => c.Timeout=TimeSpan.FromSeconds(10))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect=false, UseCookies=false, UseProxy=false });
        services.AddSingleton(p => new DispatcharrSettingsStore(Path.Combine(p.GetRequiredService<IApplicationPaths>().DataPath,"3picFin","dispatcharr.json")));
        services.AddSingleton(p => new DispatcharrClient(p.GetRequiredService<IHttpClientFactory>().CreateClient("3picFin.Dispatcharr")));
    }
}
