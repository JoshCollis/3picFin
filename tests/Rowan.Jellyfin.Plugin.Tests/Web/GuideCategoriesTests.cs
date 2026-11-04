using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.LiveTv;
using Rowan.Jellyfin.Plugin.Web;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Web;

public sealed class GuideCategoriesTests
{
    private static readonly Guid Allowed = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Hidden = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void ExactAssignedGroupsOnlyAndVisibleIntersectionWithStableNamesAndIds()
    {
        var assignments = new[] { new GuideAssignedGroup("external-a", "UK General"), new GuideAssignedGroup("external-b", "UK News") };
        var visible = new[] { new GuideCategoryChannel(Allowed, "external-a") };
        var result = GuideCategoryPolicy.Select(visible, assignments);
        Assert.True(result.Available);
        var group = Assert.Single(result.Items);
        Assert.Equal("UK General", group.Name);
        Assert.Equal(new[] { Allowed.ToString("N") }, group.ChannelIds);
        Assert.DoesNotContain(Hidden.ToString("N"), group.ChannelIds);
        Assert.Equal(group.Id, Assert.Single(GuideCategoryPolicy.Select(visible, assignments.Reverse()).Items).Id);
        Assert.Matches("^group-[a-f0-9]{64}$", group.Id);
        Assert.Empty(GuideCategoryPolicy.Select([], assignments).Items);
    }

    [Fact]
    public void MissingAndAmbiguousJoinsNeverGuessByChannelNameNumberPrefixOrGenre()
    {
        var visible = new[] { new GuideCategoryChannel(Allowed, "m3u_exact") };
        Assert.Empty(GuideCategoryPolicy.Select(visible, [new("m3u_other", "UK General")]).Items);
        Assert.Empty(GuideCategoryPolicy.Select(visible, [new("m3u_exact", "")]).Items);
        Assert.False(GuideCategoryPolicy.Select(visible, [new("m3u_exact", "UK General"), new("m3u_exact", "UK News")]).Available);
        Assert.False(GuideCategoryPolicy.Select([visible[0], new(Hidden, "m3u_exact")], []).Available);
        Assert.False(GuideCategoryPolicy.Select(visible, [new("m3u_exact", "https://private.invalid/secret")]).Available);
        Assert.False(GuideCategoryPolicy.Select(visible, [new("m3u_exact", new string('x', 201))]).Available);
        Assert.False(GuideCategoryPolicy.Select([], Enumerable.Range(0,10001).Select(i => new GuideAssignedGroup(i.ToString(), "Group"))).Available);
    }

    [Fact]
    public async Task NativeCacheFirstArrayAcceptsTrailingRemnantAndNeverReturnsPrivateFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fin-category-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var host = new TunerHostInfo { Id = Guid.NewGuid().ToString("N"), Type = "m3u" };
        var file = Path.Combine(directory, host.Id + "_channels");
        try
        {
            // Native serializer's OpenWrite can leave bytes from a longer previous array.
            var native = "[{\"Name\":\"USA\",\"Number\":\"2\",\"Id\":\"m3u_exact\",\"TunerHostId\":\"" + host.Id +
                "\",\"ChannelGroup\":\"UK General\",\"Tags\":[\"Kids\"],\"Path\":\"https://private.invalid/secret\"}]";
            await File.WriteAllTextAsync(file, native + new string('x',3057));
            var rows = await GuideCategoryPolicy.ReadCache(directory, [host], CancellationToken.None);
            Assert.Equal(new GuideAssignedGroup("m3u_exact", "UK General"), Assert.Single(rows));
            var publicData = System.Text.Json.JsonSerializer.Serialize(GuideCategoryPolicy.Select([new(Allowed,"m3u_exact")], rows));
            Assert.DoesNotContain("secret", publicData);
            Assert.DoesNotContain("Kids", publicData);
            await File.WriteAllTextAsync(file, native[..^1]);
            await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => GuideCategoryPolicy.ReadCache(directory,[host],CancellationToken.None));
            await File.WriteAllTextAsync(file, native.Replace(host.Id, Guid.NewGuid().ToString("N")));
            await Assert.ThrowsAsync<InvalidDataException>(() => GuideCategoryPolicy.ReadCache(directory,[host],CancellationToken.None));
            await File.WriteAllBytesAsync(file, new byte[16*1024*1024+1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => GuideCategoryPolicy.ReadCache(directory,[host],CancellationToken.None));
        }
        finally { File.Delete(file); Directory.Delete(directory); }
    }
}
