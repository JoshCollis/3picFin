using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;
using Rowan.Jellyfin.Plugin.Assistant;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Assistant;

public class LiveTvAssistantTests
{
    public class Proxy : DispatchProxy
    {
        public Func<string, object?[], object?> Call = (_, _) => throw new NotSupportedException();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!.Name, args!);
    }
    private static T Fake<T>(Func<string, object?[], object?> call) where T : class
    { var value = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)value).Call = call; return value; }

    [Fact]
    public async Task GuideUsesIndexedFullFutureScopeAndChannelCardsAreRevalidated()
    {
        var user = new User("test", "test", "test"); user.SetPermission(PermissionKind.EnableLiveTvAccess, true);
        var id = Guid.NewGuid(); var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "BBC One", Number = "101" };
        var now = DateTime.UtcNow;
        var program = new LiveTvProgram { Id = Guid.NewGuid(), ChannelId = channel.Id, Name = "Future fixture", StartDate = now.AddDays(30), EndDate = now.AddDays(30).AddHours(1) };
        var visible = true;
        var users = Fake<IUserManager>((_, _) => user);
        var live = Fake<ILiveTvManager>((name, _) => name == "IsEnabledForUser" ? true : new Folder { Id = Guid.NewGuid() });
        var libraries = Fake<ILibraryManager>((name, args) =>
        {
            if (name == "GetItemById") return channel;
            var query = Assert.IsType<InternalItemsQuery>(args[0]);
            Assert.Same(user, query.User); Assert.Null(query.MaxStartDate); Assert.Equal("Future", query.SearchTerm);
            Assert.Equal(new[] { BaseItemKind.LiveTvProgram }, query.IncludeItemTypes); Assert.Equal(26, query.Limit);
            return new QueryResult<BaseItem> { Items = new BaseItem[] { program } };
        });
        var service = new AiLiveTv(users, live, libraries, (_, _) => visible);
        var result = JsonSerializer.SerializeToElement(service.Guide(id, "Future", null, "upcoming", null, null, "all", 1, TimeZoneInfo.Utc, default), AiJson.Options);
        Assert.Equal("101", Assert.Single(result.GetProperty("items").EnumerateArray()).GetProperty("channelNumber").GetString());
        var tools = new AiTools(null!, null!, null!, null!, liveTv: service);
        var turn = new AiTools.Turn(id, new(), new(), () => true);
        var key = "channel:" + channel.Id.ToString("N");
        await tools.Run(turn, "get_title", JsonSerializer.SerializeToElement(new { key }), default);
        await tools.Run(turn, "show_titles", JsonSerializer.SerializeToElement(new { keys = new[] { key } }), default);
        Assert.Equal("channel", Assert.Single(turn.Response.Cards).MediaType);
        visible = false; Assert.Null(service.GetChannel(id, channel.Id));
        user.SetPermission(PermissionKind.EnableLiveTvAccess, false);
        Assert.Throws<AiException>(() => service.GetChannel(id, channel.Id));
    }

    [Fact]
    public void ChannelSearchFindsNumbersBeyondHomeInventoryLimit()
    {
        var user = new User("test", "test", "test"); user.SetPermission(PermissionKind.EnableLiveTvAccess, true);
        var channels = Enumerable.Range(0, 1600).Select(n => (BaseItem)new LiveTvChannel { Id = Guid.NewGuid(), Name = "Channel " + n, Number = n.ToString() }).ToArray();
        var live = Fake<ILiveTvManager>((name, args) =>
        {
            if (name == "IsEnabledForUser") return true;
            var query = Assert.IsType<LiveTvChannelQuery>(args[0]);
            return new QueryResult<BaseItem> { Items = channels.Skip(query.StartIndex ?? 0).Take(query.Limit ?? 500).ToArray() };
        });
        var service = new AiLiveTv(Fake<IUserManager>((_, _) => user), live, null!, (_, _) => true);
        var result = JsonSerializer.SerializeToElement(service.Channels(Guid.NewGuid(), "1599", 1, default), AiJson.Options);
        Assert.Equal("1599", Assert.Single(result.GetProperty("items").EnumerateArray()).GetProperty("number").GetString());
        Assert.True(result.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void FreshDiscoveryUsesTypedDateFiltersAndRejectsInjectionAndOffsetFreeTimes()
    {
        var today = new DateOnly(2026, 11, 9);
        var movies = AiCatalog.Browse("recent_movies", 2, "Science Fiction", null, null, today);
        Assert.Contains("genre=878", movies.Path); Assert.Contains("primaryReleaseDateLte=2026-11-09", movies.Path);
        Assert.Contains("firstAirDateGte=2026-11-09", AiCatalog.Browse("upcoming_tv", 1, "", null, null, today).Path);
        Assert.Throws<FormatException>(() => AiCatalog.Browse("movies", 1, "Comedy&adult=true", null, null, today));
        Assert.Throws<FormatException>(() => AiCatalog.Browse("trending", 1, "Comedy", null, null, today));
        Assert.Throws<FormatException>(() => AiLiveTv.Date("2026-11-09T12:00:00"));
        Assert.Equal(TimeSpan.FromHours(1), AiLiveTv.Date("2026-11-09T12:00:00+01:00")!.Value.Offset);
    }
}
