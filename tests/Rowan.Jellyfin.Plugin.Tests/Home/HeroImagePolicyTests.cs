using System;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Home;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class HeroImagePolicyTests
{
    [Fact]
    public void ImageRouteHasAuthenticationAndNoCallerUserOrPath()
    {
        var type = typeof(HeroImageController);
        Assert.NotNull(type.GetCustomAttributes(typeof(AuthorizeAttribute), true).SingleOrDefault());
        var method = type.GetMethod("GetImage")!;
        Assert.Equal("Hero/Image/{itemId:guid}", Assert.Single(method.GetCustomAttributes(typeof(HttpGetAttribute), true).Cast<HttpGetAttribute>()).Template);
        Assert.Equal(2, method.GetParameters().Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async System.Threading.Tasks.Task UserlessClaimCannotResolveImage(string? claim)
    {
        var controller = new HeroImageController(null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity(claim is null ? [] : [new Claim("Jellyfin-UserId", claim)], "test")) } };
        Assert.IsType<ForbidResult>(await controller.GetImage(Guid.NewGuid(), "tag"));
    }


    [Fact]
    public void ImageIdentityRejectsInvalidInput()
    {
        Assert.False(HeroImagePolicy.ValidRequest(Guid.Empty, "tag"));
        Assert.False(HeroImagePolicy.ValidRequest(Guid.NewGuid(), null));
        Assert.False(HeroImagePolicy.ValidRequest(Guid.NewGuid(), "../tag"));
        Assert.True(HeroImagePolicy.ValidRequest(Guid.NewGuid(), "a1B2"));
    }

    [Fact]
    public void RejectsForeignOrInvisibleItemAndWrongImageTag()
    {
        var item = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid() };
        var library = Guid.NewGuid();
        Assert.False(HeroImagePolicy.VisibleImage(item, [library], false, "tag", "tag"));
        Assert.False(HeroImagePolicy.VisibleImage(item, [library], true, "wrong", "tag"));
        Assert.False(HeroImagePolicy.VisibleImage(item, [library], true, "tag", "tag"));
    }
}
