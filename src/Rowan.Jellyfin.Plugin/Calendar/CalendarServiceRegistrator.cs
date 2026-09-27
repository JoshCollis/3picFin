using System.Net.Http;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Rowan.Jellyfin.Plugin.Configuration;

namespace Rowan.Jellyfin.Plugin.Calendar;

public sealed class CalendarServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddHttpClient("Rowan.ArrCalendar")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<CalendarAdmission>();
        services.AddTransient(provider => new ArrCalendarClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("Rowan.ArrCalendar"),
            ArrCalendarOptions.TryFromConfiguration(Plugin.Current?.Configuration ?? new PluginConfiguration()),
            provider.GetRequiredService<CalendarAdmission>()));
    }
}
