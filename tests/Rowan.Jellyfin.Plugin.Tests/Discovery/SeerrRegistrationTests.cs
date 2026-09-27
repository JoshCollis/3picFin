using System;
using System.Net.Http;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Rowan.Jellyfin.Plugin.Discovery;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Discovery;

public sealed class SeerrRegistrationTests
{
    [Fact]
    public void RegistratorResolvesClientWithRedirectsDisabled()
    {
        var registrator = new SeerrServiceRegistrator();
        var services = new ServiceCollection();
        registrator.RegisterServices(services, null!);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<SeerrClient>());
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient("Rowan.Seerr");
        // The configured primary handler must not be the default redirect-following handler.
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("Rowan.Seerr");
        var primary = handler;
        while (primary is DelegatingHandler delegating) primary = delegating.InnerHandler!;
        Assert.False(Assert.IsType<HttpClientHandler>(primary).AllowAutoRedirect);
    }
}
