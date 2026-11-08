using System.IO;
using System.Net.Http;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Rowan.Jellyfin.Plugin.Assistant;

public sealed class AiServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddHttpClient("3picFin.Ai")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton(p => new AiStore(Path.Combine(p.GetRequiredService<IApplicationPaths>().DataPath, "3picFin", "assistant")));
        services.AddHostedService<AiRetention>();
        services.AddTransient(p => new AiClient(p.GetRequiredService<IHttpClientFactory>().CreateClient("3picFin.Ai")));
        services.AddTransient<AiLibrary>();
        services.AddTransient<AiTools>();
        services.AddTransient<AiConversation>();
        services.AddTransient<AiRequestConfirmation>();
    }
}
