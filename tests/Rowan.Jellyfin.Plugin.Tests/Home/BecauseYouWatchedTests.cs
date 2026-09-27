using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Configuration;
using Rowan.Jellyfin.Plugin.Home;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class BecauseYouWatchedTests
{
    [Fact]
    public void FeatureIsIndependentAndOffByDefault()
    {
        Assert.False(new PluginConfiguration { HomeEnabled = true }.BecauseYouWatchedRowEnabled);
        Assert.NotNull(typeof(BecauseYouWatchedController).GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal(new[] { "cancellationToken" }, typeof(BecauseYouWatchedController).GetMethod("GetRows")!.GetParameters().Select(p => p.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    public async System.Threading.Tasks.Task UserlessKeyIsRejectedBeforeDependenciesAreTouched(string? claim)
    {
        var controller = new BecauseYouWatchedController(null!, null!, null!, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity(claim is null ? [] : [new Claim("Jellyfin-UserId", claim)], "test"))
        } };
        Assert.IsType<ForbidResult>((await controller.GetRows(default)).Result);
    }

    [Fact]
    public void DisjointLibrariesAndRestrictedParentCannotProduceSeedsOrResults()
    {
        var alice = Guid.NewGuid(); var bob = Guid.NewGuid();
        var own = new MovieItem(alice); var foreign = new MovieItem(bob);
        var restricted = new MovieItem(alice);
        var seeds = BecauseYouWatchedPolicy.SelectSeeds([own, foreign, restricted], [alice],
            item => item != restricted, _ => [], 5);
        Assert.Equal([own], seeds);
        Assert.Equal([foreign], BecauseYouWatchedPolicy.SelectSeeds([own, foreign, restricted], [bob],
            _ => true, _ => [], 5));
        Assert.Empty(BecauseYouWatchedPolicy.SelectResults(own, [foreign, restricted, own],
            [alice], item => item != restricted, _ => false, 16)); // seed itself excluded
    }

    [Fact]
    public void SeedsFromSameCollectionAreNotRepeated()
    {
        var library = Guid.NewGuid(); var collection = Guid.NewGuid();
        var first = new MovieItem(library); var second = new MovieItem(library); var third = new MovieItem(library);
        Assert.Equal([first, third], BecauseYouWatchedPolicy.SelectSeeds([first, second, third], [library],
            _ => true, item => item == third ? [] : [collection], 5));
    }

    [Fact]
    public void SeedWithOverflowingCollectionMembershipFailsClosed()
    {
        var library = Guid.NewGuid();
        var seed = new MovieItem(library);
        Assert.Empty(BecauseYouWatchedPolicy.SelectSeeds([seed], [library], _ => true,
            _ => Enumerable.Range(0, 33).Select(_ => Guid.NewGuid()).ToArray(), 5));
    }

    [Fact]
    public void BoundedRefillPastHiddenFirstPageAndMissingLibrary()
    {
        var library = Guid.NewGuid(); var calls = 0;
        var hidden = Enumerable.Range(0, 8).Select(_ => (BaseItem)new MovieItem(library)).ToArray();
        var allowed = new MovieItem(library);
        var found = BecauseYouWatchedPolicy.CollectSeeds([library], (id, start, count) => {
            calls++;
            return start == 0 ? hidden : [allowed];
        }, item => item == allowed, _ => [], 8, 1);
        Assert.Equal([allowed], found);
        Assert.Equal(2, calls);
        calls = 0;
        Assert.Empty(BecauseYouWatchedPolicy.CollectSeeds([], (_, _, _) => { calls++; return []; }, _ => true, _ => [], 8, 5));
        Assert.Equal(0, calls);
        calls = 0;
        BecauseYouWatchedPolicy.CollectSeeds([library], (_, _, count) => {
            calls++; return Enumerable.Range(0, count).Select(_ => (BaseItem)new MovieItem(library)).ToArray();
        }, _ => false, _ => [], 8, 5);
        Assert.Equal(8, calls);
    }

    [Fact]
    public void SeedsIncludeLaterVisibleLibraryEvenWhenFirstHasFiveCandidates()
    {
        var firstLibrary = Guid.NewGuid(); var secondLibrary = Guid.NewGuid();
        var first = Enumerable.Range(0, 5).Select(_ => new MovieItem(firstLibrary)).ToArray();
        var later = new MovieItem(secondLibrary);
        var calls = new List<Guid>();
        var seeds = BecauseYouWatchedPolicy.CollectSeeds([firstLibrary, secondLibrary], (library, start, count) => {
            calls.Add(library);
            return start == 0 ? library == firstLibrary ? first : [later] : [];
        }, _ => true, _ => [], 5, 5);
        Assert.Contains(secondLibrary, calls);
        Assert.Contains(later, seeds);
        Assert.Equal(5, seeds.Length);
    }

    [Fact]
    public void MultiLibrarySelectionSkipsSharedCollectionsAndKeepsQueryCaps()
    {
        var firstLibrary = Guid.NewGuid(); var secondLibrary = Guid.NewGuid();
        var collection = Guid.NewGuid();
        var first = new MovieItem(firstLibrary); var overlapping = new MovieItem(secondLibrary);
        var distinct = new MovieItem(secondLibrary);
        var calls = new Dictionary<Guid, int>();
        var seeds = BecauseYouWatchedPolicy.CollectSeeds([firstLibrary, secondLibrary], (library, start, count) => {
            calls[library] = calls.GetValueOrDefault(library) + 1;
            Assert.Equal(2, count);
            if (library == firstLibrary) return start == 0 ? [first] : [];
            return start == 0 ? [overlapping, distinct] : [];
        }, _ => true, movie => movie == distinct ? [] : [collection], 2, 5);
        Assert.Contains(distinct, seeds);
        Assert.Single(seeds, movie => movie == first || movie == overlapping);
        Assert.Equal(2, seeds.Length);
        Assert.Equal(1, calls[firstLibrary]);
        Assert.Equal(2, calls[secondLibrary]);
    }

    [Fact]
    public void ResultsKeepProviderOrderButRejectPlayedForeignAndParentRestricted()
    {
        var library = Guid.NewGuid(); var foreignLibrary = Guid.NewGuid();
        var seed = new MovieItem(library); var restricted = new MovieItem(library);
        var watched = new MovieItem(library); var good = new MovieItem(library);
        var foreign = new MovieItem(foreignLibrary);
        Assert.Equal([good], BecauseYouWatchedPolicy.SelectResults(seed,
            [foreign, restricted, watched, seed, good], [library],
            item => item != restricted, item => item == watched, 16, hideWatched: true));
        Assert.Equal([watched, good], BecauseYouWatchedPolicy.SelectResults(seed,
            [foreign, restricted, watched, seed, good], [library],
            item => item != restricted, item => item == watched, 16, hideWatched: false));
    }

    [Fact]
    public async Task ProviderFailureAfterSuccessfulSeedKeepsEarlierResult()
    {
        var library = Guid.NewGuid();
        var first = new MovieItem(library); var second = new MovieItem(library);
        var card = new MovieItem(library); var calls = 0;
        var results = await BecauseYouWatchedPolicy.GatherResultsAsync([first, second],
            () => [library], _ => true, _ => true,
            (_, _) => {
                calls++;
                if (calls == 2) throw new InvalidOperationException("provider secret");
                return Task.FromResult<IEnumerable<BaseItem>>([card]);
            }, 16, false, default);
        Assert.Single(results);
        Assert.Same(first, results[0].Seed);
        Assert.Equal([card], results[0].Items);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RevokedSeedIsNotPassedToProvider()
    {
        var library = Guid.NewGuid(); var seed = new MovieItem(library);
        var candidates = BecauseYouWatchedPolicy.CollectSeeds([library],
            (_, _, _) => [seed], _ => true, _ => [], 15, 5);
        Assert.Equal([seed], candidates);
        var providerCalled = false;
        // Permission changes after candidate gathering but before the provider boundary.
        var results = await BecauseYouWatchedPolicy.GatherResultsAsync(candidates,
            () => [], _ => true, _ => true,
            (_, _) => { providerCalled = true; return Task.FromResult<IEnumerable<BaseItem>>([]); },
            16, false, default);
        Assert.Empty(results);
        Assert.False(providerCalled);
    }

    [Fact]
    public async Task RevokedSeedAfterProviderIsNotExposed()
    {
        var library = Guid.NewGuid(); var seed = new MovieItem(library);
        var visible = true;
        var results = await BecauseYouWatchedPolicy.GatherResultsAsync([seed],
            () => visible ? [library] : [], _ => true, _ => true,
            (_, _) => { visible = false; return Task.FromResult<IEnumerable<BaseItem>>([]); },
            16, false, default);
        Assert.Empty(results);
    }

    [Fact]
    public async Task LaterProviderRevocationRemovesEarlierCard()
    {
        var library = Guid.NewGuid(); var first = new MovieItem(library); var second = new MovieItem(library);
        var revoked = new MovieItem(library); var retained = new MovieItem(library);
        var allowed = true;
        var gathered = await BecauseYouWatchedPolicy.GatherResultsAsync([first, second],
            () => [library], _ => true, _ => true,
            (seed, _) => {
                if (seed == second) allowed = false;
                return Task.FromResult<IEnumerable<BaseItem>>(seed == first ? [revoked, retained] : [retained]);
            }, 16, false, default);
        var rows = BecauseYouWatchedPolicy.FinalizeRows<string>(gathered,
            () => [library], item => item != revoked || allowed, _ => true,
            item => item.Id.ToString(), item => item.Id.ToString(), default);
        Assert.Equal(2, rows.Count);
        Assert.Equal([retained.Id.ToString()], rows[0].Items);
    }

    [Fact]
    public void LaterConversionRevocationRemovesEarlierSeed()
    {
        var library = Guid.NewGuid(); var first = new MovieItem(library); var second = new MovieItem(library);
        var card = new MovieItem(library); var allowed = true;
        var rows = BecauseYouWatchedPolicy.FinalizeRows<string>(
            [(first, [card]), (second, [card])], () => [library],
            item => item != first || allowed, _ => true,
            item => item.Id.ToString(), item => {
                if (item == card) allowed = false;
                return item.Id.ToString();
            }, default);
        Assert.Single(rows);
        Assert.Same(second, rows[0].Seed);
    }

    [Fact]
    public void ConversionRevocationRemovesCardAndEmptyRow()
    {
        var library = Guid.NewGuid(); var seed = new MovieItem(library); var card = new MovieItem(library);
        var allowed = true;
        var rows = BecauseYouWatchedPolicy.FinalizeRows<string>([(seed, [card])],
            () => [library], item => item != card || allowed, _ => true,
            item => item.Id.ToString(), item => { allowed = false; return item.Id.ToString(); },
            default);
        Assert.Empty(rows);
    }

    [Fact]
    public void LaterConversionRevocationRemovesEarlierCardButKeepsOtherCards()
    {
        var library = Guid.NewGuid(); var first = new MovieItem(library); var second = new MovieItem(library);
        var revoked = new MovieItem(library); var retained = new MovieItem(library); var later = new MovieItem(library);
        var allowed = true;
        var rows = BecauseYouWatchedPolicy.FinalizeRows<string>(
            [(first, [revoked, retained]), (second, [later])],
            () => [library], item => item != revoked || allowed, _ => true,
            item => item.Id.ToString(), item => {
                if (item == later) allowed = false;
                return item.Id.ToString();
            }, default);
        Assert.Equal(2, rows.Count);
        Assert.Equal([retained.Id.ToString()], rows[0].Items);
        Assert.Equal([later.Id.ToString()], rows[1].Items);
    }

    [Fact]
    public async Task IgnoredCancellationAfterProviderReturnPropagates()
    {
        var library = Guid.NewGuid(); var seed = new MovieItem(library);
        using var source = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BecauseYouWatchedPolicy.GatherResultsAsync(
            [seed], () => [library], _ => true, _ => true,
            (_, _) => { source.Cancel(); return Task.FromResult<IEnumerable<BaseItem>>([]); },
            16, false, source.Token));
    }

    [Fact]
    public async Task ProviderFailureAfterCancellationStillPropagates()
    {
        var library = Guid.NewGuid(); var seed = new MovieItem(library);
        using var source = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BecauseYouWatchedPolicy.GatherResultsAsync(
            [seed], () => [library], _ => true, _ => true,
            (_, _) => { source.Cancel(); throw new InvalidOperationException("provider failed after cancellation"); },
            16, false, source.Token));
    }

    [Fact]
    public async Task CancellationWithNoResultsPropagates()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BecauseYouWatchedPolicy.GatherResultsAsync(
            Array.Empty<Movie>(), () => [], _ => true, _ => true,
            (_, _) => Task.FromResult<IEnumerable<BaseItem>>([]), 16, false, source.Token));
    }

    [Fact]
    public void CancellationDuringFinalValidationPropagatesEvenWithNoRows()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => BecauseYouWatchedPolicy.FinalizeRows<string>(
            [], () => [], _ => true, _ => true, _ => "seed", _ => "card", source.Token));
    }

    private sealed class MovieItem : Movie
    {
        private readonly Guid _library;
        public MovieItem(Guid library) { _library = library; Id = Guid.NewGuid(); }
        public override IEnumerable<Guid> GetAncestorIds() => [_library];
    }
}
