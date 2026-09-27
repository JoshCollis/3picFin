using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using MediaBrowser.Controller.Entities;
using Rowan.Jellyfin.Plugin.Home;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class RecentlyAddedPolicyTests
{
    private static readonly Guid First = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Second = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly CollectionFolder FirstLibrary = new() { Id = First, Name = "First" };
    private static readonly CollectionFolder SecondLibrary = new() { Id = Second, Name = "Second" };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void MissingOrInvalidUserClaimIsRejected(string? claim)
    {
        var principal = Principal(claim);
        Assert.False(RecentlyAddedPolicy.TryGetUserId(principal, out var id));
        Assert.Equal(Guid.Empty, id);
    }

    [Fact]
    public void ValidUserClaimIsAccepted()
    {
        Assert.True(RecentlyAddedPolicy.TryGetUserId(Principal(First.ToString()), out var id));
        Assert.Equal(First, id);
    }

    [Fact]
    public void SelectionCannotAddLibrariesInvisibleToEachUser()
    {
        var aliceVisible = new[] { FirstLibrary };
        var bobVisible = new[] { SecondLibrary };
        Assert.Equal([First], RecentlyAddedPolicy.SelectVisible(aliceVisible, null).Select(x => x.Id));
        Assert.Equal([Second], RecentlyAddedPolicy.SelectVisible(bobVisible, [First, Second]).Select(x => x.Id));
        Assert.Empty(RecentlyAddedPolicy.SelectVisible(bobVisible, [First]));
    }

    [Fact]
    public void ExplicitEmptySelectionAndDisabledHomeYieldNoLibraries()
    {
        Assert.Empty(RecentlyAddedPolicy.SelectVisible([FirstLibrary], []));
        Assert.Empty(RecentlyAddedPolicy.SelectVisible([FirstLibrary], null, homeEnabled: false));
    }

    [Fact]
    public void NullSelectionIncludesAllVisibleLibrariesOnlyOnce()
    {
        Assert.Equal([First, Second], RecentlyAddedPolicy.SelectVisible([FirstLibrary, SecondLibrary, FirstLibrary], null)
            .Select(x => x.Id));
    }

    [Fact]
    public void MalformedPersistedSelectionFailsClosedInsteadOfThrowingOrBroadening()
    {
        foreach (Guid[] ids in new[] { new[] { First, Guid.Empty }, new[] { First, First } })
        {
            Assert.False(RecentlyAddedPolicy.TrySelectVisible([FirstLibrary, SecondLibrary], ids, out var selected));
            Assert.Empty(selected);
        }
    }

    [Fact]
    public void MissingSelectedParentAfterQueryDiscardsFallbackResults()
    {
        var foreign = new TestItem(Second);
        var groups = new[] { Tuple.Create<BaseItem, List<BaseItem>>(null!, [foreign]) };
        Assert.Empty(RecentlyAddedPolicy.SafeLatestItems(FirstLibrary, groups, _ => null));
    }

    [Fact]
    public void WrongResolvedParentDoesNotAuthorizeSelectedResults()
    {
        var selected = new TestItem(First);
        var groups = new[] { Tuple.Create<BaseItem, List<BaseItem>>(null!, [selected]) };
        Assert.Empty(RecentlyAddedPolicy.SafeLatestItems(FirstLibrary, groups, _ => SecondLibrary));
    }

    [Fact]
    public void MixedFallbackResultsNeverIncludeItemsOutsideSelectedLibrary()
    {
        var selected = new TestItem(First);
        var foreign = new TestItem(Second);
        var groups = new[]
        {
            Tuple.Create<BaseItem, List<BaseItem>>(null!, [selected]),
            Tuple.Create<BaseItem, List<BaseItem>>(null!, [foreign]),
            Tuple.Create<BaseItem, List<BaseItem>>(null!, [])
        };
        Assert.Equal([selected], RecentlyAddedPolicy.SafeLatestItems(FirstLibrary, groups, _ => FirstLibrary));
    }

    private sealed class TestItem(Guid ancestor) : BaseItem
    {
        public override IEnumerable<Guid> GetAncestorIds() => [ancestor];
    }

    private static ClaimsPrincipal Principal(string? userId) => new(new ClaimsIdentity(
        userId is null ? [] : [new Claim("Jellyfin-UserId", userId)], "test"));
}
