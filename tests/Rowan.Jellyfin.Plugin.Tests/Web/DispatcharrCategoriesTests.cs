using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Rowan.Jellyfin.Plugin.Web;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Web;

public sealed class DispatcharrCategoriesTests
{
    private const string Uuid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    [Theory]
    [InlineData("http://source.invalid/proxy/ts/stream/" + Uuid, true)]
    [InlineData("https://other.invalid:443/proxy/ts/stream/" + Uuid, true)]
    [InlineData("http://user:secret@source.invalid/proxy/ts/stream/" + Uuid, false)]
    [InlineData("http://source.invalid/proxy/ts/stream/" + Uuid + "?token=secret", false)]
    [InlineData("http://source.invalid/proxy/ts/stream/" + Uuid + "#fragment", false)]
    [InlineData("http://source.invalid/x/../proxy/ts/stream/" + Uuid, false)]
    [InlineData("http://source.invalid/proxy/ts/stream/%61aaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", false)]
    [InlineData("http://source.invalid/proxy/ts/stream/00000000-0000-0000-0000-000000000000", false)]
    [InlineData("file:///proxy/ts/stream/" + Uuid, false)]
    public void StreamAddressIsOnlyStrictIdentifierData(string path, bool allowed)
        => Assert.Equal(allowed, DispatcharrIdentity.TryRead(path, out _));

    [Fact]
    public void ExactUuidJoinFreshlyIntersectsVisibilityAndRejectsDuplicates()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var uuid = Guid.Parse(Uuid); var other = Guid.NewGuid();
        var cache = new[] { new DispatcharrCacheChannel("native-a", uuid), new DispatcharrCacheChannel("native-b", other) };
        var snapshot = new DispatcharrMetadata(new Dictionary<Guid, long?> { [uuid] = 4, [other] = 5 },
            new Dictionary<long, string> { [4] = "Assigned A", [5] = "Assigned B" });
        var rows = DispatcharrIdentity.Assign(cache, snapshot);
        var alice = GuideCategoryPolicy.Select([new(a, "native-a")], rows);
        var bob = GuideCategoryPolicy.Select([new(b, "native-b")], rows);
        Assert.Equal(new[] { a.ToString("N") }, Assert.Single(alice.Items).ChannelIds);
        Assert.Equal(new[] { b.ToString("N") }, Assert.Single(bob.Items).ChannelIds);
        Assert.Empty(GuideCategoryPolicy.Select([], rows).Items);
        Assert.Throws<InvalidDataException>(() => DispatcharrIdentity.Assign([cache[0], cache[0]], snapshot));
        Assert.Throws<InvalidDataException>(() => DispatcharrIdentity.Assign([cache[0], new("different", uuid)], snapshot));
        Assert.Throws<InvalidDataException>(() => DispatcharrIdentity.Assign([new("missing", Guid.NewGuid())], snapshot));
        Assert.Empty(GuideCategoryPolicy.Select([new(a,"native-a")], DispatcharrIdentity.Assign(cache,
            new(new Dictionary<Guid,long?>{[uuid]=null,[other]=null},new Dictionary<long,string>()))).Items);
    }

    [Theory]
    [InlineData("http://user:key@host/")]
    [InlineData("http://host/?key=secret")]
    [InlineData("http://host/#fragment")]
    [InlineData("http://host/%2e%2e/")]
    [InlineData("file:///secret")]
    public void InvalidManagementUrlsAreRejected(string url)
        => Assert.Throws<InvalidDataException>(() => DispatcharrConnection.Parse(url, "fixture-key"));

    [Theory]
    [InlineData("[{\"uuid\":\"" + Uuid + "\",\"channel_group_id\":4}]", "[{\"id\":4,\"name\":\"Assigned\"}]", true)]
    [InlineData("[{\"uuid\":\"" + Uuid + "\",\"channel_group_id\":null}]", "[]", true)]
    [InlineData("[{\"uuid\":\"" + Uuid + "\",\"channel_group_id\":4},{\"uuid\":\"" + Uuid + "\",\"channel_group_id\":4}]", "[{\"id\":4,\"name\":\"Assigned\"}]", false)]
    [InlineData("[{\"uuid\":\"" + Uuid + "\",\"channel_group_id\":4}]", "[{\"id\":4,\"name\":\"Assigned\"},{\"id\":4,\"name\":\"Assigned\"}]", false)]
    [InlineData("[{\"uuid\":\"" + Uuid + "\",\"channel_group_id\":4}]", "[]", false)]
    [InlineData("{\"next\":\"https://evil.invalid\",\"results\":[]}", "[]", false)]
    [InlineData("[{\"uuid\":\"" + Uuid + "\",\"uuid\":\"" + Uuid + "\",\"channel_group_id\":null}]", "[]", false)]
    public async Task NativeSummarySchemaIsStrictBoundedAndNeverFollowsNext(string channels, string groups, bool valid)
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler((request, _) => {
            paths.Add(request.RequestUri!.PathAndQuery);
            Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("X-API-Key")));
            return Task.FromResult(Json(paths.Count == 1 ? channels : groups));
        }));
        var client = new DispatcharrClient(http);
        var connection = DispatcharrConnection.Parse("http://management.invalid/prefix/", "fixture-key");
        if (valid) Assert.NotNull(await client.Fetch(connection, CancellationToken.None));
        else await Assert.ThrowsAnyAsync<InvalidDataException>(() => client.Fetch(connection, CancellationToken.None));
        Assert.InRange(paths.Count,1,2);
        Assert.Equal("/prefix/api/channels/channels/summary/", paths[0]);
        Assert.All(paths, p => Assert.DoesNotContain("proxy",p));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SourceErrorsAreSanitized(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler((_,_)=>Task.FromResult(Json("private upstream body",status))));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new DispatcharrClient(http).Fetch(
            DispatcharrConnection.Parse("http://management.invalid/", "secret"), CancellationToken.None));
        Assert.DoesNotContain("private",error.Message); Assert.DoesNotContain("management",error.Message);
    }

    [Fact]
    public async Task OversizedDeadAndCancelledSourcesAreBounded()
    {
        using var huge = new HttpClient(new Handler((_,_)=>Task.FromResult(Json(new string('x', DispatcharrClient.MaximumBytes+1)))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new DispatcharrClient(huge).Fetch(DispatcharrConnection.Parse("http://host/","key"),CancellationToken.None));
        using var dead = new HttpClient(new Handler(async (_,token)=> { await Task.Delay(Timeout.Infinite,token); return Json("[]"); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new DispatcharrClient(dead,TimeSpan.FromMilliseconds(20)).Fetch(DispatcharrConnection.Parse("http://host/","key"),CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DispatcharrClient(dead).Fetch(DispatcharrConnection.Parse("http://host/","key"),cancelled.Token));
    }

    [Fact]
    public void SettingsDefaultOffPreserveSecretsRejectStaleSavesAndClearExplicitly()
    {
        var directory=Path.Combine(Path.GetTempPath(),"fin-source-settings-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(directory,"dispatcharr.json");
        try
        {
            var store=new DispatcharrSettingsStore(path);var initial=store.Snapshot();var view=store.View();
            Assert.False(view.Enabled);Assert.False(view.HasApiKey);Assert.False(File.Exists(path));
            view=store.Save(new(true,"http://metadata.invalid/","fixture-secret",false,view.Revision));
            Assert.False(store.IsCurrent(initial));Assert.True(view.HasApiKey);
            Assert.DoesNotContain("fixture-secret",JsonSerializer.Serialize(view));
            Assert.DoesNotContain("fixture-secret",store.Snapshot().ToString());
            Assert.DoesNotContain("fixture-secret",JsonSerializer.Serialize(store.Snapshot()));
            Assert.Throws<InvalidOperationException>(()=>store.Save(new(false,"","",false,initial.Revision)));
            view=store.Save(new(true,view.BaseUrl,"",false,view.Revision));
            Assert.True(view.HasApiKey);
            Assert.True(new DispatcharrSettingsStore(path).View().HasApiKey);
            Assert.Throws<InvalidDataException>(()=>store.Save(new(true,view.BaseUrl,null,true,view.Revision)));
            view=store.Save(new(false,view.BaseUrl,null,true,view.Revision));
            Assert.False(view.HasApiKey);Assert.False(view.Enabled);
            Assert.DoesNotContain("fixture-secret",File.ReadAllText(path));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(path));
        }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
    }

    [Fact]
    public async Task MetadataCacheIsOneSnapshotRevisionScopedAndDisabledNeverReads()
    {
        var calls=0;
        using var http=new HttpClient(new Handler((_,_)=> { calls++;return Task.FromResult(Json("[]")); }));
        var client=new DispatcharrClient(http);var connection=DispatcharrConnection.Parse("http://metadata.invalid/","key");
        var first=new DispatcharrSettings(true,"http://metadata.invalid/","first",connection);
        Assert.Same(await client.Read(first,CancellationToken.None),await client.Read(first,CancellationToken.None));
        Assert.Equal(2,calls);
        await client.Read(new(true,"http://metadata.invalid/","second",connection),CancellationToken.None);Assert.Equal(4,calls);
        await Assert.ThrowsAsync<InvalidDataException>(()=>client.Read(new(false,"","third",null),CancellationToken.None));Assert.Equal(4,calls);
    }

    [Fact]
    public async Task ConcurrentRequestsCannotStartUnboundedUpstreamWork()
    {
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        using var http=new HttpClient(new Handler(async (_,token)=> { calls++;await release.Task.WaitAsync(token);return Json("[]"); }));
        var client=new DispatcharrClient(http);var settings=new DispatcharrSettings(true,"http://metadata.invalid/","one",DispatcharrConnection.Parse("http://metadata.invalid/","key"));
        var active=client.Read(settings,CancellationToken.None);
        for(var i=0;i<20;i++) await Assert.ThrowsAsync<InvalidDataException>(()=>client.Read(settings,CancellationToken.None));
        Assert.Equal(1,calls);release.SetResult();await active;Assert.Equal(2,calls);
    }

    [Fact]
    public void SameDisplayNameDoesNotMergeDistinctSourceGroupIdentities()
    {
        var a=Guid.NewGuid();var b=Guid.NewGuid();var first=Guid.NewGuid();var second=Guid.NewGuid();
        var rows=DispatcharrIdentity.Assign([new("a",first),new("b",second)],new(new Dictionary<Guid,long?>{[first]=1,[second]=2},new Dictionary<long,string>{[1]="Same",[2]="Same"}));
        var result=GuideCategoryPolicy.Select([new(a,"a"),new(b,"b")],rows);
        Assert.Equal(2,result.Items.Length);Assert.Equal(2,result.Items.Select(g=>g.Id).Distinct().Count());
        Assert.All(result.Items,g=>Assert.Single(g.ChannelIds));
    }

    [Fact]
    public async Task RealControllerReintersectsDisjointNativeVisibilityEvenWithSharedMetadataCache()
    {
        var directory=Path.Combine(Path.GetTempPath(),"fin-source-controller-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var alice=new User("alice","fixture","fixture");var bob=new User("bob","fixture","fixture");
        var a=new VisibleChannel {Id=Guid.NewGuid(),ExternalId="native-a",ServiceName="Emby",Viewer=alice.Id};
        var b=new VisibleChannel {Id=Guid.NewGuid(),ExternalId="native-b",ServiceName="Emby",Viewer=bob.Id};
        var host=new TunerHostInfo {Type="hdhomerun",Id=Guid.NewGuid().ToString("N")};var second=Guid.NewGuid();var calls=0;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory,host.Id+"_channels"),JsonSerializer.Serialize(new[]{
                new {Id=a.ExternalId,TunerHostId=host.Id,Path="http://unused.invalid/proxy/ts/stream/"+Uuid},
                new {Id=b.ExternalId,TunerHostId=host.Id,Path="http://unused.invalid/proxy/ts/stream/"+second.ToString("D")}}));
            var store=new DispatcharrSettingsStore(Path.Combine(directory,"settings.json"));store.Save(new(true,"http://management.invalid/","key",false,store.View().Revision));
            TaskCompletionSource? hold=null;
            using var http=new HttpClient(new Handler(async (request,cancellation)=> {calls++;if(hold is not null) await hold.Task.WaitAsync(cancellation);return Json(request.RequestUri!.AbsolutePath.EndsWith("summary/",StringComparison.Ordinal)?
                "[{\"uuid\":\""+Uuid+"\",\"channel_group_id\":1},{\"uuid\":\""+second+"\",\"channel_group_id\":2}]":"[{\"id\":1,\"name\":\"A\"},{\"id\":2,\"name\":\"B\"}]");}));
            var client=new DispatcharrClient(http);
            var users=Proxy<IUserManager>((method,args)=>method.Name=="GetUserById" ? ((Guid)args![0]! == alice.Id ? alice : bob) : throw new NotSupportedException());
            var live=Proxy<ILiveTvManager>((method,_)=>method.Name switch {"IsEnabledForUser"=>true,"GetInternalChannels"=>new QueryResult<BaseItem>{Items=[a,b],TotalRecordCount=2},_=>throw new NotSupportedException(method.Name)});
            var paths=DispatchProxy.Create(typeof(IServerConfigurationManager).GetProperty("ApplicationPaths")!.PropertyType,typeof(Stub));
            ((Stub)paths).Call=(method,_)=>method.Name=="get_CachePath"?directory:throw new NotSupportedException(method.Name);
            var configuration=Proxy<IServerConfigurationManager>((method,_)=>method.Name switch {"get_ApplicationPaths"=>paths,"GetConfiguration"=>new LiveTvOptions {TunerHosts=[host]},_=>throw new NotSupportedException(method.Name)});
            async Task<GuideCategories> Get(User user)
            {
                var controller=new GuideCategoriesController(users,live,configuration,store,client,()=>true){ControllerContext=new(){HttpContext=new DefaultHttpContext{
                    User=new(new ClaimsIdentity([new Claim("Jellyfin-UserId",user.Id.ToString())],"fixture"))}}};
                return Assert.IsType<GuideCategories>(Assert.IsType<OkObjectResult>((await controller.Get()).Result).Value);
            }
            Assert.Equal(new[]{a.Id.ToString("N")},Assert.Single((await Get(alice)).Items).ChannelIds);
            Assert.Equal(new[]{b.Id.ToString("N")},Assert.Single((await Get(bob)).Items).ChannelIds);
            Assert.Equal(2,calls); // Global cache holds metadata only, never a prior user's channel membership.
            a.Viewer=Guid.NewGuid();Assert.Empty((await Get(alice)).Items);Assert.Equal(2,calls);
            store.Save(new(true,"http://management.invalid/","",false,store.View().Revision));
            hold=new(TaskCreationOptions.RunContinuationsAsynchronously);var pending=Get(bob);
            b.Viewer=Guid.NewGuid();hold.SetResult();Assert.Empty((await pending).Items);Assert.Equal(4,calls);
            await File.WriteAllTextAsync(Path.Combine(directory,host.Id+"_channels"),JsonSerializer.Serialize(new[]{
                new {Id=a.ExternalId,TunerHostId=host.Id,Path="http://unused.invalid/proxy/ts/stream/"+Uuid},
                new {Id=b.ExternalId,TunerHostId=host.Id,Path="http://unused.invalid/proxy/ts/stream/"+Uuid}}));
            Assert.False((await Get(alice)).Available); // Ambiguous inventory must be an explicit, sanitized response, not HTTP 500.
        }
        finally { Directory.Delete(directory,true); }
    }

    private sealed class VisibleChannel : LiveTvChannel
    { public Guid Viewer; public override bool IsVisibleStandalone(User user)=>user.Id==Viewer; }
    public class Stub : DispatchProxy
    { public Func<MethodInfo,object?[]?,object?> Call=null!;protected override object? Invoke(MethodInfo? method,object?[]? args)=>Call(method!,args); }
    private static T Proxy<T>(Func<MethodInfo,object?[]?,object?> call) where T:class
    {var result=DispatchProxy.Create<T,Stub>();((Stub)(object)result).Call=call;return result;}

    private static HttpResponseMessage Json(string body, HttpStatusCode code=HttpStatusCode.OK) => new(code) { Content=new StringContent(body,Encoding.UTF8,"application/json") };
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken) => send(request,cancellationToken); }
}
