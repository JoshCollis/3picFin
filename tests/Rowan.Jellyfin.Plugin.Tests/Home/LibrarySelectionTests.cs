using System;
using Rowan.Jellyfin.Plugin.Home;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class LibrarySelectionTests
{
    private static readonly Guid First = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Second = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Other = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void UnsetAllowlistIncludesAnyLibrary()
    {
        var selection = new LibrarySelection(null);

        Assert.True(selection.Includes(First));
        Assert.True(selection.Includes(Other));
    }

    [Fact]
    public void ExplicitEmptyAllowlistIncludesNoLibraries()
    {
        var selection = new LibrarySelection([]);

        Assert.False(selection.Includes(First));
        Assert.False(selection.Includes(Other));
    }

    [Fact]
    public void MultipleSelectedIdsIncludeOnlyMatches()
    {
        var selection = new LibrarySelection([First, Second]);

        Assert.True(selection.Includes(First));
        Assert.True(selection.Includes(Second));
        Assert.False(selection.Includes(Other));
    }

    [Fact]
    public void EmptyGuidInConfiguredIdsIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new LibrarySelection([First, Guid.Empty]));
    }

    [Fact]
    public void DuplicateConfiguredIdsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new LibrarySelection([First, Second, First]));
    }

    [Fact]
    public void MutatingConfiguredArrayDoesNotChangeSelection()
    {
        Guid[] configuredIds = [First, Second];
        var selection = new LibrarySelection(configuredIds);

        configuredIds[0] = Other;
        configuredIds[1] = Guid.Empty;

        Assert.True(selection.Includes(First));
        Assert.True(selection.Includes(Second));
        Assert.False(selection.Includes(Other));
    }
}
