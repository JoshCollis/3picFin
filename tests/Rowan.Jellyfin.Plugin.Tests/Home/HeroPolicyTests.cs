using System;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Home;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class HeroPolicyTests
{
    [Fact]
    public void EndpointRequiresAuthorizationWithoutCallerControlledIdentityOrParent()
    {
        var type = typeof(HeroController);
        Assert.NotNull(type.GetCustomAttributes(typeof(AuthorizeAttribute), true).SingleOrDefault());
        Assert.Equal("Rowan/Home", ((RouteAttribute)type.GetCustomAttributes(typeof(RouteAttribute), true).Single()).Template);
        var method = type.GetMethod("GetHero")!;
        Assert.Equal("Hero", ((HttpGetAttribute)method.GetCustomAttributes(typeof(HttpGetAttribute), true).Single()).Template);
        Assert.Empty(method.GetParameters());
        Assert.NotNull(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ResponseCacheAttribute), true)
            .Cast<Microsoft.AspNetCore.Mvc.ResponseCacheAttribute>().SingleOrDefault(x => x.NoStore && x.Location == Microsoft.AspNetCore.Mvc.ResponseCacheLocation.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("bogus")]
    public void MissingOrInvalidUserClaimIsForbidden(string? claim)
    {
        var controller = new HeroController(null!, null!, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity(claim is null ? [] : [new Claim("Jellyfin-UserId", claim)], "test")) } };
        Assert.IsType<ForbidResult>(controller.GetHero().Result);
    }

    [Fact]
    public void SelectionOmitsNonVisualItemsAndCapsOutput()
    {
        var items = Enumerable.Range(0, 40).Select(i => new BaseItemDto {
            Id = Guid.NewGuid(), Name = $"Item {i}", Type = i == 0 ? BaseItemKind.Audio : BaseItemKind.Movie,
            BackdropImageTags = i == 1 ? [] : ["backdrop"], Overview = "Summary" }).ToArray();
        var result = HeroPolicy.SelectSlides(items, Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), new DateOnly(2026, 9, 26));
        Assert.Equal(12, result.Count);
        Assert.DoesNotContain(result, x => x.Id == items[0].Id || x.Id == items[1].Id);
        Assert.All(result, slide => { Assert.Equal("Backdrop", slide.ImageType); Assert.Equal(0, slide.ImageIndex); Assert.Equal("backdrop", slide.ImageTag); });
        Assert.Equal(result, HeroPolicy.SelectSlides(items, Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), new DateOnly(2026, 9, 26)));
    }

    [Fact]
    public void RotationChangesWithDayAndDoesNotReturnUrlsOrTrailerData()
    {
        var items = Enumerable.Range(0, 30).Select(i => new BaseItemDto {
            Id = Guid.NewGuid(), Name = $"Item {i}", Type = BaseItemKind.Series, BackdropImageTags = ["tag"] }).ToArray();
        var user = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var today = HeroPolicy.SelectSlides(items, user, new DateOnly(2026, 9, 26));
        var tomorrow = HeroPolicy.SelectSlides(items, user, new DateOnly(2026, 9, 27));
        Assert.NotEqual(today.Select(x => x.Id), tomorrow.Select(x => x.Id));
        Assert.DoesNotContain("Trailer", string.Join(',', typeof(HeroSlide).GetProperties().Select(p => p.Name)));
        Assert.DoesNotContain("Url", string.Join(',', typeof(HeroSlide).GetProperties().Select(p => p.Name)));
    }
}
