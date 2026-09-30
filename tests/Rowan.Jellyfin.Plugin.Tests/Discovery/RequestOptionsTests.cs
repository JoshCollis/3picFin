using System;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Discovery;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Discovery;

public sealed class RequestOptionsTests
{
    private static readonly Guid UserId = Guid.Parse("f6b48a38-9e4b-4b1b-a957-e8e425e91922");

    [Fact]
    public async Task DetailAllowsOnlyMappedPermissionsAndValidatedSeasons()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(req => {
            calls++;
            if (calls == 1) return Json("{\"id\":42,\"permissions\":524288}");
            Assert.Equal("42", string.Join(",", req.Headers.GetValues("X-API-User")));
            Assert.EndsWith("/api/v1/tv/9", req.RequestUri!.AbsolutePath);
            return Json("{\"id\":9,\"seasons\":[{\"seasonNumber\":0},{\"seasonNumber\":1},{\"seasonNumber\":2},{\"seasonNumber\":3}],\"mediaInfo\":{\"status\":4,\"status4k\":1,\"seasons\":[{\"seasonNumber\":2,\"status\":5,\"status4k\":1}],\"requests\":[]},\"apiKey\":\"secret\"}");
        }));
        var result = Assert.IsType<OkObjectResult>((await Controller(http).GetRequestOptions("tv", 9)).Result);
        var value = Assert.IsType<RequestOptions>(result.Value);
        Assert.True(value.CanRequest);
        Assert.False(value.CanRequest4k);
        Assert.Equal(new[] { 1, 3 }, value.Seasons);
        Assert.Equal(4, value.MediaStatus);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(value));
    }

    [Fact]
    public async Task OptionsExposeBothVariantStatusesAndSuppressBlocklistedPermission()
    {
        foreach (var status in new[] { 1, 5, 6 })
        {
            var calls = 0;
            using var http = new HttpClient(new Handler(_ => Json(++calls == 1
                ? "{\"id\":42,\"permissions\":3106}"
                : $"{{\"id\":9,\"mediaInfo\":{{\"status\":{status},\"status4k\":5,\"requests\":[]}}}}")));
            var result = Assert.IsType<OkObjectResult>((await Controller(http).GetRequestOptions("movie", 9)).Result);
            var value = Assert.IsType<RequestOptions>(result.Value);
            Assert.Equal(status, value.MediaStatus);
            Assert.Equal(5, value.MediaStatus4k);
            Assert.Equal(status is not (5 or 6), value.CanRequest);
            Assert.False(value.CanRequest4k);
        }
    }

    [Fact]
    public async Task MissingIdentityOrMalformedDetailsFailClosed()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Json(calls == 1 ? "{\"id\":42,\"permissions\":2}" : "{\"id\":9,\"seasons\":[{\"seasonNumber\":1},{\"seasonNumber\":1}]"); }));
        Assert.IsType<ForbidResult>((await Controller(http, false).GetRequestOptions("tv", 9)).Result);
        Assert.Equal(0, calls);
        Assert.IsType<BadRequestResult>((await Controller(http).GetRequestOptions("tv", 0)).Result);
        Assert.Equal(0, calls);
        Assert.IsType<StatusCodeResult>((await Controller(http).GetRequestOptions("tv", 9)).Result);
        Assert.Equal(2, calls);
    }

    private static DiscoveryController Controller(HttpClient http, bool authenticated = true)
    {
        var options = SeerrOptions.FromConfiguration(new PluginConfiguration { SeerrEnabled = true, SeerrBaseUrl = "https://seerr.example", SeerrApiKey = "example-secret" });
        var controller = new DiscoveryController(new SeerrClient(http, options), _ => true);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(authenticated ? [new Claim("Jellyfin-UserId", UserId.ToString())] : [], authenticated ? "test" : null)) } };
        return controller;
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
}
