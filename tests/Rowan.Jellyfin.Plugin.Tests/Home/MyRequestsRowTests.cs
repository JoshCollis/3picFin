using System;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using Rowan.Jellyfin.Plugin.Home;
using Rowan.Jellyfin.Plugin.Discovery;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class MyRequestsRowTests
{
    private static SeerrResult Source(string json) => SeerrResult.Success(JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public async System.Threading.Tasks.Task UserlessRequestIsForbiddenBeforeAccessingDependencies()
    {
        Assert.False(new Rowan.Jellyfin.Plugin.Configuration.PluginConfiguration().MyRequestsRowEnabled);
        Assert.False(new Rowan.Jellyfin.Plugin.Configuration.PluginConfiguration().MyRequestsHideWatched);
        var controller = new MyRequestsRowController(null!, null!, null!, null!, null!, null!);
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
        Assert.IsType<Microsoft.AspNetCore.Mvc.ForbidResult>((await controller.GetRow(default)).Result);
    }

    [Fact]
    public void ExtractsOnlyAvailableApprovedPersonalMediaIds()
    {
        var own = Guid.NewGuid(); var pending = Guid.NewGuid(); var unavailable = Guid.NewGuid();
        var json = System.Text.Json.JsonSerializer.Serialize(new { results = new object[] { new { status = 2, media = new { jellyfinMediaId = own.ToString(), status = 5 } }, new { status = 1, media = new { jellyfinMediaId = pending.ToString(), status = 5 } }, new { status = 2, media = new { jellyfinMediaId = unavailable.ToString(), status = 3 } }, new { status = 5, media = new { jellyfinMediaId = own.ToString(), status = 5 } } } });
        var result = MyRequestsRowPolicy.RequestedIds(Source(json));
        Assert.Equal([own], result.Ids);
        Assert.Null(result.Error);
    }

    [Fact]
    public void MissingIdsAndMalformedUpstreamFailClosed()
    {
        Assert.Empty(MyRequestsRowPolicy.RequestedIds(Source("{\"results\":[{\"status\":5,\"media\":{\"jellyfinMediaId\":\"\",\"status\":5}}]}")).Ids);
        Assert.Equal("UpstreamUnavailable", MyRequestsRowPolicy.RequestedIds(Source("{\"results\":{}}")).Error);
        Assert.Equal("UpstreamUnavailable", MyRequestsRowPolicy.RequestedIds(Source("{\"results\":[null]}")).Error);
        Assert.Empty(MyRequestsRowPolicy.RequestedIds(Source("{\"results\":[{\"status\":\"secret\",\"media\":{}}]}")).Ids);
        Assert.Empty(MyRequestsRowPolicy.RequestedIds(Source("{\"results\":[{\"status\":2,\"media\":{\"status\":\"invalid\"}}]}")).Ids);
        Assert.Equal("UpstreamUnavailable", MyRequestsRowPolicy.RequestedIds(SeerrResult.Fail(SeerrFailure.UpstreamUnavailable)).Error);
    }

    [Fact]
    public void DisjointLibraryAndParentalVetoCannotLeakForeignOrHiddenItems()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var first = new Item(a, true); var second = new Item(b, true); var hidden = new Item(a, true);
        var ids = new[] { first.Id, second.Id, hidden.Id };
        Assert.Equal([first], MyRequestsRowPolicy.Select([first, second, hidden], ids, [a], x => x != hidden, false, _ => false));
        Assert.Equal([second], MyRequestsRowPolicy.Select([first, second, hidden], ids, [b], _ => true, false, _ => false));
        Assert.Empty(MyRequestsRowPolicy.Select([first], [], [a], _ => true, false, _ => false));
        first.IsVirtualItem = true;
        Assert.Empty(MyRequestsRowPolicy.Select([first], ids, [a], _ => true, false, _ => false));
    }

    [Fact]
    public void WatchedFilterDedupeNewestAndSixteenCap()
    {
        var library = Guid.NewGuid();
        var items = Enumerable.Range(0, 20).Select(i => new Item(library, true) { DateCreated = new DateTime(2030, 1, 1).AddDays(i) }).ToArray();
        var ids = items.Select(x => x.Id).ToArray();
        var selected = MyRequestsRowPolicy.Select(items.Concat([items[19]]), ids, [library], _ => true, true, x => x == items[19]);
        Assert.Equal(16, selected.Length);
        Assert.Equal(items[18], selected[0]);
        Assert.DoesNotContain(items[19], selected);
    }

    private sealed class Item(Guid library) : BaseItem
    {
        private readonly Guid _library = library;
        public Item(Guid library, bool assignId) : this(library) { Id = Guid.NewGuid(); }
        public override System.Collections.Generic.IEnumerable<Guid> GetAncestorIds() => [_library];
        public Item() : this(Guid.Empty) { }
    }
}
