using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Querying;
using Rowan.Jellyfin.Plugin.Discovery;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Discovery;

public sealed class TitleLibraryQueryTests
{
    private static readonly User Viewer = new("fixture", "fixture", "fixture");
    private static readonly Guid[] Roots = Enumerable.Range(1, 12).Select(Id).ToArray();
    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private sealed record Fixture(string Name, TitleLibraryCandidate Candidate, bool Virtual = false);
    private static Fixture Skywalker(int id = 9000, string type = "movie", bool visible = true) =>
        new("Star Wars: The Rise of Skywalker", new(Id(id), type, "181812", visible, Roots[11]));

    [Theory]
    [InlineData("movie")]
    [InlineData("tv")]
    public void ExactDependencyProviderPredicateFindsTitleBeyond256ItemsAndEightRoots(string type)
    {
        var fixtures = Enumerable.Range(100, 3600).Select(n => new Fixture(
            "Star Wars: The Rise of Skywalker", new(Id(n), type, n.ToString(), true, Roots[n % 12]))).ToList();
        fixtures.Add(Skywalker(type: type));
        fixtures.Add(Skywalker(9001, type == "movie" ? "tv" : "movie"));
        fixtures.Add(Skywalker(9002) with { Virtual = true });
        var calls = 0;
        var result = TitleLibraryPolicy.Lookup(Viewer, Roots, type, 181812, Id(9999), query =>
        {
            calls++;
            Assert.Same(Viewer, query.User);
            Assert.Equal(33, query.Limit);
            Assert.False(query.EnableTotalRecordCount);
            Assert.False(query.GroupByPresentationUniqueKey);
            Assert.True(query.IncludeAlternateVersions);
            Assert.False(query.IsVirtualItem);
            Assert.Equal(Roots, query.AncestorIds);
            Assert.Contains(ItemFields.ProviderIds, query.DtoOptions.Fields);
            Assert.Equal(type == "movie" ? BaseItemKind.Movie : BaseItemKind.Series, Assert.Single(query.IncludeItemTypes));
            Assert.Equal("181812", query.HasAnyProviderId!["Tmdb"]);
            // Execute the real Jellyfin 12.1 predicate from the installed dependency, before Take.
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var database = new ProviderDatabase(new DbContextOptionsBuilder<ProviderDatabase>().UseSqlite(connection).Options);
            database.Database.EnsureCreated();
            database.AddRange(fixtures.Select(f => new BaseItemEntity { Id = f.Candidate.Id, Type = f.Candidate.Type,
                Provider = [new BaseItemProvider { Item = null!, ProviderId = "Tmdb", ProviderValue = f.Candidate.TmdbId! }] }));
            database.SaveChanges();
            var entities = database.Set<BaseItemEntity>();
            var filtered = entities.WhereHasAnyProviderId(query.HasAnyProviderId!)
                .Where(e => e.Type == type).Take(query.Limit!.Value);
            var sql = filtered.ToQueryString();
            Assert.Contains("EXISTS", sql);
            Assert.Contains("LIMIT", sql);
            var matching = filtered.Select(e => e.Id).ToHashSet();
            return fixtures.Where(f => matching.Contains(f.Candidate.Id) && !f.Virtual &&
                    f.Candidate.Type == type && f.Candidate.Visible && query.AncestorIds.Contains(f.Candidate.RootId))
                .Take(query.Limit!.Value).Select(f => f.Candidate);
        }, c => c.Visible && Roots.Contains(c.RootId));
        Assert.Equal(1, calls);
        Assert.Equal(new TitleLibraryResolution(Id(9000), "present"), result);
    }

    [Fact]
    public void MissingOrStaleHintAndDuplicateOrderCannotHideAccessibleCopy()
    {
        var first = Skywalker().Candidate;
        var second = Skywalker(9001).Candidate;
        foreach (var hint in new Guid?[] { null, Id(9999), first.Id })
        foreach (var items in new[] { new[] { first, second }, new[] { second, first } })
            Assert.Equal(first.Id, Lookup(items, hint).ItemId);
        Assert.Equal(second.Id, Lookup([first, second], second.Id).ItemId);
        Assert.Equal(second.Id, TitleLibraryPolicy.Lookup(Viewer, Roots, "movie", 181812, first.Id,
            _ => [first, second], c => c.Id != first.Id).ItemId);
    }

    [Fact]
    public void BoundedOverflowIsUnknownAndEmptyCompleteQueryIsAbsent()
    {
        Assert.Equal(new TitleLibraryResolution(null, "unknown"), Lookup(Enumerable.Range(9000, 33).Select(n => Skywalker(n).Candidate)));
        Assert.Equal(new TitleLibraryResolution(null, "absent"), Lookup([]));
        Assert.Equal(new TitleLibraryResolution(null, "absent"), TitleLibraryPolicy.Lookup(Viewer, [], "movie", 181812, null,
            _ => throw new Exception("must not query with empty roots"), _ => true));
    }

    [Fact]
    public void WrongProviderTypeRestrictedAndRevokedMatchesNeverExposeIds()
    {
        var match = Skywalker().Candidate;
        foreach (var candidate in new[] { match with { Visible = false }, match with { Type = "tv" },
                     match with { TmdbId = "42" }, match with { RootId = Id(9999) } })
            Assert.Null(Lookup([candidate], candidate.Id).ItemId);
        Assert.Equal(new TitleLibraryResolution(null, "unknown"), TitleLibraryPolicy.Lookup(Viewer, Roots, "movie", 181812, match.Id,
            _ => [match], _ => false)); // user/root/item/ancestor gate revoked between query and return
    }

    // Minimal in-memory relational schema for the real dependency's provider predicate.
    // This is SQL translation/execution coverage, not a hosted Jellyfin integration test.
    private sealed class ProviderDatabase(DbContextOptions<ProviderDatabase> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var item = modelBuilder.Entity<BaseItemEntity>();
            foreach (var property in typeof(BaseItemEntity).GetProperties())
                if (property.Name is not ("Id" or "Type" or "Provider")) item.Ignore(property.Name);
            item.HasKey(e => e.Id);
            var provider = modelBuilder.Entity<BaseItemProvider>();
            provider.HasKey(e => new { e.ItemId, e.ProviderId });
            provider.HasOne(e => e.Item).WithMany(e => e.Provider).HasForeignKey(e => e.ItemId);
        }
    }

    private static TitleLibraryResolution Lookup(IEnumerable<TitleLibraryCandidate> candidates, Guid? hint = null) =>
        TitleLibraryPolicy.Lookup(Viewer, Roots, "movie", 181812, hint, _ => candidates,
            c => c.Visible && Roots.Contains(c.RootId));
}
