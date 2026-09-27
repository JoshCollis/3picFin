using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Home;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class RecentlyAddedControllerTests
{
    [Fact]
    public void EndpointRequiresAuthorizationAndHasNoCallerSelectedIds()
    {
        var type = typeof(RecentlyAddedController);
        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.Equal("Rowan/Home", type.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        var method = type.GetMethod("GetRecentlyAdded")!;
        Assert.Equal("RecentlyAdded", method.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Empty(method.GetParameters());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void ApiKeyOrInvalidClaimCannotGetRows(string? claim)
    {
        var controller = new RecentlyAddedController(null!, null!, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    claim is null ? [] : [new Claim("Jellyfin-UserId", claim)], "test"))
            }
        };
        Assert.IsType<ForbidResult>(controller.GetRecentlyAdded().Result);
    }
}
