using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Home;
using Rowan.Jellyfin.Plugin.Configuration;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class NativeRowsTests
{
    [Fact]
    public void ExposesOnlyNamedBoundedRowsWithoutCallerUserOrParent()
    {
        var type = typeof(NativeRowsController);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal("Rowan/Home", type.GetCustomAttribute<RouteAttribute>()?.Template);
        var action = type.GetMethod("GetRow")!;
        Assert.Equal("Rows/{kind}", action.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal(new[] { "kind" }, action.GetParameters().Select(p => p.Name));
        Assert.Equal(new[] { "ContinueWatching", "NextUp", "LatestMovies", "LatestShows", "MyMedia", "ContinueWatchingNextUp", "Collections" }, NativeRowsPolicy.Kinds);
        Assert.False(NativeRowsPolicy.IsSupported("Unknown"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void UserlessKeyCannotRetrieveRows(string? claim)
    {
        var controller = new NativeRowsController(null!, null!, null!, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity(claim is null ? [] : [new Claim("Jellyfin-UserId", claim)], "test"))
        } };
        Assert.IsType<ForbidResult>(controller.GetRow("NextUp").Result);
        Assert.IsType<ForbidResult>(controller.GetRow("ContinueWatchingNextUp").Result);
        Assert.IsType<ForbidResult>(controller.GetRow("Collections").Result);
    }

    [Fact]
    public void NewRowsAreIndependentlyDisabledByDefault()
    {
        var config = new PluginConfiguration { HomeEnabled = true };
        Assert.False(config.CombinedPlaybackRowEnabled);
        Assert.False(config.CollectionsRowEnabled);
        Assert.False(config.CombinedPlaybackHideWatched);
    }

    [Fact]
    public void ForeignItemIsExcludedEvenIfQueryReturnsIt()
    {
        var allowed = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var own = new TestItem(allowed);
        var other = new TestItem(foreign);
        Assert.Equal(new BaseItem[] { own }, NativeRowsPolicy.WithinVisibleLibraries(
            [own, other], [allowed], 12));
    }

    [Fact]
    public void TwoDisjointUsersSeeOnlyOwnItemsAfterSharedQuery()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var aliceItem = new TestItem(alice);
        var bobItem = new TestItem(bob);
        BaseItem[] mixed = [aliceItem, bobItem];
        Assert.Equal([aliceItem], NativeRowsPolicy.WithinVisibleLibraries(mixed, [alice], 12));
        Assert.Equal([bobItem], NativeRowsPolicy.WithinVisibleLibraries(mixed, [bob], 12));
        Assert.Empty(NativeRowsPolicy.WithinVisibleLibraries(mixed, [], 12));
    }

    [Fact]
    public void RowLimitCapsOutput()
    {
        var allowed = Guid.NewGuid();
        var items = Enumerable.Range(0, 40).Select(_ => (BaseItem)new TestItem(allowed)).ToArray();
        Assert.Equal(12, NativeRowsPolicy.WithinVisibleLibraries(items, [allowed], 12).Length);
    }

    [Fact]
    public void ParentalVetoDropsCandidateBeforeDtoConversion()
    {
        var library = Guid.NewGuid();
        var episode = new TestItem(library);
        Assert.Equal([episode], NativeRowsPolicy.WithinVisibleLibraries([episode], [library], 12));
        Assert.Empty(NativeRowsPolicy.WithinVisibleLibraries([episode], [library], 12,
            item => item != episode)); // Simulates Jellyfin standalone visibility veto from a restricted parent.
    }

    [Fact]
    public void LatestRowsRefillPastFilteredPagesAndDeduplicateSeries()
    {
        var library = Guid.NewGuid();
        var seriesA = Guid.NewGuid();
        var seriesB = Guid.NewGuid();
        var episodes = Enumerable.Range(0, 22).Select(_ => (BaseItem)new RowEpisode(library, seriesA)).ToArray();
        var unseen = new RowEpisode(library, seriesB);
        var pages = episodes.Concat([unseen]).Chunk(8).ToArray();
        var result = NativeRowsPolicy.CollectLatest(8, 2, true, [library], (_, start, count) =>
            pages.Skip(start / count).FirstOrDefault() ?? [], _ => true);
        Assert.Equal([episodes[0], unseen], result);
    }

    [Fact]
    public void LatestMoviesRefillAfterParentalFiltering()
    {
        var library = Guid.NewGuid();
        var hidden = Enumerable.Range(0, 8).Select(_ => (BaseItem)new TestItem(library)).ToArray();
        var allowed = new TestItem(library);
        var result = NativeRowsPolicy.CollectLatest(8, 1, false, [library],
            (_, start, _) => start == 0 ? hidden : [allowed], item => item == allowed);
        Assert.Equal([allowed], result);
    }

    [Fact]
    public void LatestShowsExcludeUnairedEpisodes()
    {
        var library = Guid.NewGuid();
        var future = new RowEpisode(library, Guid.NewGuid()) { PremiereDate = DateTime.UtcNow.AddDays(1) };
        var aired = new RowEpisode(library, Guid.NewGuid()) { PremiereDate = DateTime.UtcNow.AddDays(-2) };
        Assert.Equal([aired], NativeRowsPolicy.CollectLatest(8, 2, true, [library], (_, _, _) => [future, aired], _ => true));
    }

    [Fact]
    public void SameDayEpisodeWithKnownElapsedAirTimeIsIncludedButDateOnlyIsNot()
    {
        var library = Guid.NewGuid();
        var now = new DateTime(2030, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var aired = new RowEpisode(library, Guid.NewGuid()) { PremiereDate = now.AddHours(-1) };
        var upcoming = new RowEpisode(library, Guid.NewGuid()) { PremiereDate = now.AddHours(1) };
        var dateOnly = new RowEpisode(library, Guid.NewGuid()) { PremiereDate = now.Date };
        Assert.Equal([aired], NativeRowsPolicy.CollectLatest(8, 3, true, [library],
            (_, _, _) => [upcoming, dateOnly, aired], _ => true, nowUtc: now));
    }

    [Fact]
    public void NextUpAcceptsKnownElapsedAirTimeButRejectsAmbiguousAndVirtualEpisodes()
    {
        var library = Guid.NewGuid();
        var now = new DateTime(2030, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var airedAt = now.AddHours(-1);
        var ambiguous = new RowEpisode(library, Guid.NewGuid()) { PremiereDate = DateTime.SpecifyKind(airedAt, DateTimeKind.Unspecified) };
        var virtualEpisode = new RowEpisode(library, Guid.NewGuid()) { PremiereDate = airedAt, IsVirtualItem = true };
        var aired = new RowEpisode(library, Guid.NewGuid()) { PremiereDate = airedAt };
        Assert.Equal([aired], NativeRowsPolicy.CollectPaged(4, 1, [library],
            (_, _) => [ambiguous, virtualEpisode, aired], _ => true, true, nowUtc: now));
    }

    [Fact]
    public void LatestShowsRanksSeriesByNewestEligibleEpisodeAcrossLibraries()
    {
        var firstLibrary = Guid.NewGuid();
        var secondLibrary = Guid.NewGuid();
        var seriesA = Guid.NewGuid();
        var olderA = new RowEpisode(firstLibrary, seriesA) { PremiereDate = DateTime.UtcNow.AddDays(-4) };
        var rival = new RowEpisode(firstLibrary, Guid.NewGuid()) { PremiereDate = DateTime.UtcNow.AddDays(-2) };
        var newerA = new RowEpisode(secondLibrary, seriesA) { PremiereDate = DateTime.UtcNow.AddDays(-1) };
        var a = new RowSeries(firstLibrary, seriesA);
        var b = new RowSeries(firstLibrary, rival.SeriesId);
        var result = NativeRowsPolicy.CollectLatest(4, 2, true, [firstLibrary, secondLibrary],
            (library, _, _) => library == firstLibrary ? [olderA, rival] : [newerA],
            _ => true, episode => episode.SeriesId == seriesA ? a : b);
        Assert.Equal([a, b], result);
    }

    [Fact]
    public void LatestShowsDoesNotPromoteNewerEpisodeFromHiddenLibrary()
    {
        var firstLibrary = Guid.NewGuid();
        var secondLibrary = Guid.NewGuid();
        var series = Guid.NewGuid();
        var allowed = new RowEpisode(firstLibrary, series) { PremiereDate = DateTime.UtcNow.AddDays(-3) };
        var hidden = new RowEpisode(secondLibrary, series) { PremiereDate = DateTime.UtcNow.AddDays(-1) };
        var visibleSeries = new RowSeries(firstLibrary, series);
        Assert.Equal([visibleSeries], NativeRowsPolicy.CollectLatest(2, 1, true,
            [firstLibrary, secondLibrary], (library, _, _) => library == firstLibrary ? [allowed] : [hidden],
            item => item != hidden, _ => visibleSeries));
    }

    [Fact]
    public void NextUpRefillsPastUnairedAndForeignEpisodes()
    {
        var library = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var future = new RowEpisode(library, Guid.NewGuid()) { PremiereDate = DateTime.UtcNow.AddDays(2) };
        var unavailable = new RowEpisode(library, Guid.NewGuid()) { IsVirtualItem = true };
        var elsewhere = new RowEpisode(foreign, Guid.NewGuid());
        var aired = new RowEpisode(library, Guid.NewGuid()) { PremiereDate = DateTime.UtcNow.AddDays(-2) };
        var starts = new System.Collections.Generic.List<int>();
        var result = NativeRowsPolicy.CollectPaged(2, 1, [library], (start, _) =>
        {
            starts.Add(start);
            return start switch { 0 => [future, unavailable], 2 => [elsewhere, aired], _ => [] };
        }, _ => true, true);
        Assert.Equal([aired], result);
        Assert.Equal([0, 2], starts);
    }

    [Fact]
    public void ContinueWatchingRefillsAfterParentalVetoAndDeduplication()
    {
        var library = Guid.NewGuid();
        var hidden = new TestItem(library);
        var allowed = new TestItem(library);
        var result = NativeRowsPolicy.CollectPaged(2, 1, [library], (start, _) =>
            start == 0 ? [hidden, hidden] : [allowed], item => item != hidden, false);
        Assert.Equal([allowed], result);
    }

    [Fact]
    public void PagedRowsStopAtEightPagesWhenEverythingIsFiltered()
    {
        var calls = 0;
        var library = Guid.NewGuid();
        var result = NativeRowsPolicy.CollectPaged(2, 1, [library], (_, _) =>
        {
            calls++;
            return [new TestItem(library), new TestItem(library)];
        }, _ => false, false);
        Assert.Empty(result);
        Assert.Equal(8, calls);
    }

    [Fact]
    public void LatestShowsRefillWhenResolvedSeriesFailsEntitlement()
    {
        var library = Guid.NewGuid();
        var first = new RowEpisode(library, Guid.NewGuid());
        var second = new RowEpisode(library, Guid.NewGuid());
        var allowed = new RowSeries(library, second.SeriesId);
        var starts = new System.Collections.Generic.List<int>();
        var result = NativeRowsPolicy.CollectLatest(1, 1, true, [library], (_, start, _) =>
        {
            starts.Add(start);
            return start == 0 ? [first] : start == 1 ? [second] : [];
        }, _ => true, episode => episode == first ? null : allowed);
        Assert.Equal([allowed], result);
        Assert.Equal([0, 1], starts);
    }

    [Fact]
    public void LatestShowsRejectResolvedSeriesOutsideEligibleLibrary()
    {
        var library = Guid.NewGuid();
        var episode = new RowEpisode(library, Guid.NewGuid());
        var foreign = new RowSeries(Guid.NewGuid(), episode.SeriesId);
        Assert.Empty(NativeRowsPolicy.CollectLatest(2, 1, true, [library], (_, _, _) => [episode],
            _ => true, _ => foreign));
    }

    [Fact]
    public void LatestShowsRejectMismatchedResolvedSeries()
    {
        var library = Guid.NewGuid();
        var episode = new RowEpisode(library, Guid.NewGuid());
        var wrong = new RowSeries(library, Guid.NewGuid());
        Assert.Empty(NativeRowsPolicy.CollectLatest(2, 1, true, [library], (_, _, _) => [episode],
            _ => true, _ => wrong));
    }

    [Fact]
    public void UserViewsMapToLegitimateLibrariesWithoutViewIdEquality()
    {
        var movieLibrary = Guid.NewGuid();
        var tvLibrary = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var specific = new UserView { Id = Guid.NewGuid(), DisplayParentId = movieLibrary };
        var grouped = new UserView { Id = Guid.NewGuid(), ViewType = CollectionType.tvshows };
        var forged = new UserView { Id = Guid.NewGuid(), DisplayParentId = foreign };
        Assert.Equal([specific, grouped], NativeRowsPolicy.VisibleViews(
            [specific, grouped, forged], [movieLibrary, tvLibrary],
            [new NativeRowsPolicy.GroupedLibrary(tvLibrary, CollectionType.tvshows)]));
        Assert.Equal([specific], NativeRowsPolicy.VisibleViews(
            [specific, grouped, forged], [movieLibrary, tvLibrary], []));
    }

    [Fact]
    public void CombinedRowDeduplicatesFiltersWatchedAndSortsByPlaybackWithSeriesFallback()
    {
        var now = DateTime.UtcNow;
        var resumed = new BaseItemDto { Id = Guid.NewGuid(), DateCreated = now.AddDays(-10), UserData = new() { Key = "resumed", LastPlayedDate = now.AddDays(-2) } };
        var next = new BaseItemDto { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid(), DateCreated = now.AddDays(-30) };
        var watched = new BaseItemDto { Id = Guid.NewGuid(), UserData = new() { Key = "watched", Played = true } };
        var result = NativeRowsPolicy.MergePlayback([resumed, watched], [resumed, next], true, 12,
            id => id == next.SeriesId ? now.AddDays(-1) : null);
        Assert.Equal([next.Id, resumed.Id], result.Select(item => item.Id));
    }

    [Fact]
    public void CombinedRowLimitsResultsAndOnlyLooksUpDistinctMissingSeries()
    {
        var series = Guid.NewGuid();
        var episodes = Enumerable.Range(0, 40).Select(_ => new BaseItemDto { Id = Guid.NewGuid(), SeriesId = series }).ToArray();
        var calls = 0;
        Assert.Equal(12, NativeRowsPolicy.MergePlayback([], episodes, false, 12, _ => { calls++; return null; }).Length);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CollectionsRequireVisibleCollectionFolderAndAuthorizedMember()
    {
        var folder = Guid.NewGuid(); var movieLibrary = Guid.NewGuid(); var foreignLibrary = Guid.NewGuid();
        var own = new RowBoxSet(folder); var foreign = new RowBoxSet(folder);
        var wrongFolder = new RowBoxSet(Guid.NewGuid());
        var member = new TestItem(movieLibrary); var foreignMember = new TestItem(foreignLibrary);
        var result = NativeRowsPolicy.CollectCollections(2, 16, [folder], [movieLibrary],
            (id, start, _) => start == 0 ? [foreign, wrongFolder] : [own],
            box => box == own ? [member] : [foreignMember], item => item != foreignMember);
        Assert.Equal([own], result);
    }

    [Fact]
    public void CollectionsRefillWithBoundedPagesAndCap()
    {
        var folder = Guid.NewGuid(); var movie = Guid.NewGuid(); var calls = 0;
        var result = NativeRowsPolicy.CollectCollections(2, 1, [folder], [movie], (_, _, _) => {
            calls++; return [new RowBoxSet(folder), new RowBoxSet(folder)];
        }, _ => [new TestItem(movie)], _ => false);
        Assert.Empty(result);
        Assert.Equal(8, calls);
    }

    [Fact]
    public void CollectionsRankAuthorizedBoxesGloballyAfterFirstFolderFillsRow()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var media = Guid.NewGuid();
        var old = Enumerable.Range(0, 16).Select(i => new RowBoxSet(first) {
            DateLastMediaAdded = new DateTime(2020, 1, 16).AddDays(-i)
        }).ToArray();
        var newest = new RowBoxSet(second) { DateLastMediaAdded = new DateTime(2030, 1, 1) };
        var hidden = new RowBoxSet(second) { DateLastMediaAdded = newest.DateLastMediaAdded!.Value.AddDays(1) };
        var foreignMember = new RowBoxSet(second) { DateLastMediaAdded = newest.DateLastMediaAdded!.Value.AddDays(2) };
        var wrongRoot = new RowBoxSet(first) { DateLastMediaAdded = newest.DateLastMediaAdded!.Value.AddDays(3) };
        var member = new TestItem(media);
        var result = NativeRowsPolicy.CollectCollections(16, 16, [first, second], [media],
            (folder, start, _) => folder == first ? (start == 0 ? old : []) :
                (start == 0 ? [hidden, foreignMember, wrongRoot, newest] : []),
            box => box == foreignMember ? [new TestItem(Guid.NewGuid())] : [member],
            item => item != hidden);
        Assert.Equal(16, result.Length);
        Assert.Same(newest, result[0]);
        Assert.DoesNotContain(old[^1], result);
        Assert.DoesNotContain(hidden, result);
        Assert.DoesNotContain(foreignMember, result);
        Assert.DoesNotContain(wrongRoot, result);
    }

    [Fact]
    public void CollectionsScanRemainsBoundedWhenEachFolderHasEnoughCandidates()
    {
        var folders = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        var media = Guid.NewGuid();
        var calls = new System.Collections.Generic.Dictionary<Guid, int>();
        NativeRowsPolicy.CollectCollections(2, 1, folders, [media], (folder, _, _) => {
            calls[folder] = (calls.TryGetValue(folder, out var count) ? count : 0) + 1;
            return [new RowBoxSet(folder), new RowBoxSet(folder)];
        }, _ => [new TestItem(media)], _ => true);
        Assert.Equal(folders.Take(8), calls.Keys);
        Assert.All(calls.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void CollectionsCapNumberOfFoldersQueried()
    {
        var folders = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        var queried = new System.Collections.Generic.HashSet<Guid>();
        NativeRowsPolicy.CollectCollections(2, 16, folders, [Guid.NewGuid()], (folder, _, _) => {
            queried.Add(folder); return [];
        }, _ => [], _ => true);
        Assert.True(queried.Count <= 8);
    }

    private sealed class RowBoxSet : BoxSet
    {
        private readonly Guid folder;
        public RowBoxSet(Guid folder) { this.folder = folder; Id = Guid.NewGuid(); }
        public override System.Collections.Generic.IEnumerable<Guid> GetAncestorIds() => [folder];
    }

    private sealed class RowSeries : Series
    {
        private readonly Guid library;
        public RowSeries(Guid library, Guid id) { this.library = library; Id = id; }
        public override System.Collections.Generic.IEnumerable<Guid> GetAncestorIds() => [library];
    }

    private sealed class RowEpisode : Episode
    {
        private readonly Guid library;
        public RowEpisode(Guid library, Guid series) { this.library = library; SeriesId = series; Id = Guid.NewGuid(); }
        public override System.Collections.Generic.IEnumerable<Guid> GetAncestorIds() => [library];
    }

    private sealed class TestItem : BaseItem
    {
        public TestItem(Guid ancestor) { this.ancestor = ancestor; Id = Guid.NewGuid(); }
        private readonly Guid ancestor;
        public override System.Collections.Generic.IEnumerable<Guid> GetAncestorIds() => [ancestor];
    }
}
