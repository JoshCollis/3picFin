using System;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Home;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class LiveTvRowTests
{
    [Fact]
    public void LiveTvIsAnIndependentDefaultOffSelfScopedRoute()
    {
        Assert.False(new PluginConfiguration { HomeEnabled = true }.LiveTvRowEnabled);
        Assert.NotNull(typeof(LiveTvRowController).GetCustomAttributes(typeof(AuthorizeAttribute), false).Single());
        Assert.Equal("Rows/LiveTV", typeof(LiveTvRowController).GetMethod("GetRow")!.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>().Single().Template);
        Assert.Empty(typeof(LiveTvRowController).GetMethod("GetRow")!.GetParameters());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void NoRealUserClaimIsForbidden(string? claim)
    {
        var controller = new LiveTvRowController(null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            claim is null ? [] : [new Claim("Jellyfin-UserId", claim)], "test")) } };
        Assert.IsType<ForbidResult>(controller.GetRow().Result);
    }

    [Fact]
    public void ProgramsRequireCurrentEntitledChannelAndCurrentAiringTime()
    {
        var now = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var own = new LiveTvChannel { Id = Guid.NewGuid(), Name = "Channel <one>" };
        var foreign = new LiveTvChannel { Id = Guid.NewGuid(), Name = "Foreign" };
        var good = new LiveTvProgram { Id = Guid.NewGuid(), ChannelId = own.Id, Name = "Now", StartDate = now.AddMinutes(-30), EndDate = now.AddMinutes(30) };
        var other = new LiveTvProgram { Id = Guid.NewGuid(), ChannelId = foreign.Id, Name = "Secret", StartDate = good.StartDate, EndDate = good.EndDate };
        var future = new LiveTvProgram { Id = Guid.NewGuid(), ChannelId = own.Id, Name = "Later", StartDate = now.AddMinutes(1), EndDate = now.AddHours(1) };
        var result = LiveTvRowPolicy.Select([good, other, future], [own], _ => true, now);
        Assert.Single(result);
        Assert.Equal(good.Id, result[0].ProgramId);
        Assert.Equal(own.Id, result[0].ChannelId);
        Assert.Equal("Now", result[0].Title);
        Assert.Equal("Channel <one>", result[0].ChannelName);
        Assert.DoesNotContain("Secret", result.Select(x => x.Title));
        Assert.Empty(LiveTvRowPolicy.Select([good], [foreign], _ => true, now));
    }

    [Fact]
    public void ParentalVetoAndEmptyGuideDoNotTurnChannelsIntoStreamLinks()
    {
        var now = DateTime.UtcNow;
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "One" };
        var program = new LiveTvProgram { Id = Guid.NewGuid(), ChannelId = channel.Id, Name = "Blocked", StartDate = now.AddMinutes(-1), EndDate = now.AddMinutes(1) };
        Assert.Empty(LiveTvRowPolicy.Select([program], [channel], item => item != program, now));
        Assert.Empty(LiveTvRowPolicy.Select([], [channel], _ => true, now));
        Assert.DoesNotContain(typeof(LiveTvCard).GetProperties(), property => property.Name.Contains("Url") || property.Name.Contains("Stream") || property.Name.Contains("Tuner"));
    }

    [Fact]
    public void LabelsAreBoundedBeforeEnteringBrowser()
    {
        var now = DateTime.UtcNow;
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = new string('C', 300), Number = new string('9', 300) };
        var program = new LiveTvProgram { Id = Guid.NewGuid(), ChannelId = channel.Id, Name = new string('P', 300),
            StartDate = now.AddMinutes(-1), EndDate = now.AddMinutes(1) };
        var card = Assert.Single(LiveTvRowPolicy.Select([program], [channel], _ => true, now));
        Assert.True(card.Title.Length <= 160);
        Assert.True(card.ChannelName.Length <= 160);
        Assert.True(card.ChannelNumber!.Length <= 16);
    }

    [Fact]
    public void CapsCandidatesAndCardsAndDeduplicates()
    {
        var now = DateTime.UtcNow;
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "One" };
        var programs = Enumerable.Range(0, 100).Select(_ => new LiveTvProgram {
            Id = Guid.NewGuid(), ChannelId = channel.Id, Name = "Show", StartDate = now.AddMinutes(-1), EndDate = now.AddMinutes(1)
        }).ToArray();
        Assert.Equal(24, LiveTvRowPolicy.Select(programs, [channel], _ => true, now).Length);
        Assert.Single(LiveTvRowPolicy.Select([programs[0], programs[0]], [channel], _ => true, now));
    }
}
