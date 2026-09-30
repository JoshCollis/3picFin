using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Rowan.Jellyfin.Plugin.Discovery;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Discovery;

public sealed class TitleLibraryAccessTests
{
    [Fact]
    public void RealAdapterUsesPhysicalRootsAndRejectsEveryRevokedGate()
    {
        var f = new Fixture();
        Assert.Equal(f.Movie.Id, f.Resolve().ItemId);
        Assert.Equal(f.Physical.Id, Assert.Single(f.Query!.AncestorIds));
        Assert.DoesNotContain(f.Collection.Id, f.Query.AncestorIds);
        foreach (var gate in new[] { "user", "collection", "physical", "parent", "item", "virtual", "identity", "ancestry", "missing" })
        {
            f = new Fixture();
            f.AfterQuery = () => {
                switch (gate)
                {
                    case "user": f.Current = null; break;
                    case "collection": f.Collection.Visible = false; break;
                    case "physical": f.Physical.Visible = false; break;
                    case "parent": f.Parent.Visible = false; break;
                    case "item": f.Movie.Visible = false; break;
                    case "virtual": f.Movie.IsVirtualItem = true; break;
                    case "identity": f.Movie.ProviderIds["Tmdb"] = "42"; break;
                    case "ancestry": f.Movie.Ancestors = []; break;
                    case "missing": f.Items.Remove(f.Movie.Id); break;
                }
            };
            Assert.Null(f.Resolve().ItemId);
        }
    }

    [Fact]
    public void HiddenCollectionNeverQueriesAndRestrictedDuplicateCannotMaskSafeCopy()
    {
        var f = new Fixture();
        f.Collection.Visible = false;
        Assert.Null(f.Resolve().ItemId);
        Assert.Null(f.Query);
        f = new Fixture();
        var hiddenParent = new VisibleFolder { Id = Guid.NewGuid(), Visible = false };
        var duplicate = new VisibleMovie { Id = Guid.NewGuid(), Ancestors = [hiddenParent.Id, f.Physical.Id],
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "181812" } };
        f.Items.Add(hiddenParent.Id, hiddenParent); f.Items.Add(duplicate.Id, duplicate);
        f.Results = [duplicate, f.Movie];
        Assert.Equal(f.Movie.Id, f.Resolve(duplicate.Id).ItemId);
    }

    [Fact]
    public void VariantFilterPreservesMissingUpgradeAndRevalidatesChangedMetadata()
    {
        var f = new Fixture();
        Assert.Equal("absent", f.Resolve(variant: _ => false).Status);
        Assert.Equal(f.Movie.Id, f.Resolve(variant: _ => true).ItemId);
        var reads = 0;
        Assert.Null(f.Resolve(variant: _ => ++reads == 1).ItemId);
        Assert.Throws<TargetInvocationException>(() => f.Resolve(variant: _ => throw new InvalidOperationException("Unknown video metadata")));
    }

    [Theory]
    [InlineData(1920, false)]
    [InlineData(2000, false)]
    [InlineData(2048, true)]
    [InlineData(3840, true)]
    [InlineData(4096, true)]
    public void Local4kUsesVideoStreamWidth(int width, bool expected)
    {
        var media = Proxy<IMediaSourceManager>((method, _) => method.Name == "GetMediaStreams" ?
            new List<MediaBrowser.Model.Entities.MediaStream> { new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Width = width } } : throw new NotSupportedException());
        var actual = typeof(TitleDetailsController).GetMethod("Is4kMovie", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [media, new Movie()]);
        Assert.Equal(expected, actual);
    }

    private sealed class Fixture
    {
        public User? Current = new("synthetic", "fixture", "fixture");
        public readonly VisibleFolder Physical = new() { Id = Guid.NewGuid() };
        public readonly VisibleFolder Parent = new() { Id = Guid.NewGuid() };
        public readonly VisibleCollection Collection = new() { Id = Guid.NewGuid() };
        public readonly VisibleMovie Movie = new() { Id = Guid.NewGuid(), ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "181812" } };
        public readonly Dictionary<Guid, BaseItem> Items = [];
        public BaseItem[] Results;
        public InternalItemsQuery? Query;
        public Action? AfterQuery;
        private readonly IUserManager _users;
        private readonly ILibraryManager _libraries;
        private readonly Guid _user;
        public Fixture()
        {
            _user = Current!.Id;
            Collection.PhysicalFolderIds = [Physical.Id];
            Movie.Ancestors = [Parent.Id, Physical.Id];
            foreach (var item in new BaseItem[] { Collection, Physical, Parent, Movie }) Items.Add(item.Id, item);
            Results = [Movie];
            _users = Proxy<IUserManager>((method, _) => method.Name == "GetUserById" ? Current : throw new NotSupportedException(method.Name));
            _libraries = Proxy<ILibraryManager>((method, args) => method.Name switch {
                "GetUserRootFolder" => new VisibleRoot { Children = [Collection] },
                "GetItemById" => Items.GetValueOrDefault((Guid)args![0]!),
                "GetItemsResult" => QueryItems((InternalItemsQuery)args![0]!),
                _ => throw new NotSupportedException(method.Name)
            });
        }
        private QueryResult<BaseItem> QueryItems(InternalItemsQuery query)
        {
            Query = query; AfterQuery?.Invoke();
            return new QueryResult<BaseItem> { Items = Results };
        }
        public TitleLibraryResolution Resolve(Guid? hint = null, Func<BaseItem, bool>? variant = null) => (TitleLibraryResolution)typeof(TitleDetailsController)
            .GetMethod("ResolveLibrary", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [_users, _libraries, _user, "movie", 181812, hint, variant])!;
    }
    private sealed class VisibleRoot : UserRootFolder
    {
        public new BaseItem[] Children = [];
        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery? query = null) => Children;
    }
    private sealed class VisibleCollection : CollectionFolder
    {
        public bool Visible = true;
        public override bool IsVisibleStandalone(User user) => Visible;
    }
    private sealed class VisibleFolder : Folder
    {
        public bool Visible = true;
        public override bool IsVisibleStandalone(User user) => Visible;
    }
    private sealed class VisibleMovie : Movie
    {
        public bool Visible = true;
        public Guid[] Ancestors = [];
        public override bool IsVisibleStandalone(User user) => Visible;
        public override IEnumerable<Guid> GetAncestorIds() => Ancestors;
    }
    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)proxy).Call = call;
        return proxy;
    }
}
