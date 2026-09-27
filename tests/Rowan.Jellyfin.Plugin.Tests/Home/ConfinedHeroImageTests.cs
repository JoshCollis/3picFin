using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Rowan.Jellyfin.Plugin.Home;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class ConfinedHeroImageTests
{
    [Fact]
    public async Task ReadsOnlyRegularFileWithinExplicitRoot()
    {
        using var tree = new Tree();
        var file = tree.Make("inside/image.jpg");
        await File.WriteAllBytesAsync(file, Jpeg);
        await using var stream = ConfinedHeroImage.TryOpen(tree.Root, file);
        Assert.NotNull(stream);
        Assert.Equal(Jpeg, await HeroImageBounds.TryReadJpeg(stream, CancellationToken.None));
        Assert.Null(ConfinedHeroImage.TryOpen(tree.Root, Path.Combine(tree.Root, "other.jpg")));
    }

    [Fact]
    public void RejectsTraversalAndPrefixCollisionAndMaliciousRoots()
    {
        using var tree = new Tree();
        var file = tree.Make("inside/image.jpg");
        File.WriteAllBytes(file, Jpeg);
        Assert.Null(ConfinedHeroImage.TryOpen(tree.Root, Path.Combine(tree.Root, "..", "outside.jpg")));
        Assert.Null(ConfinedHeroImage.TryOpen(tree.Root, tree.Root + "-evil/image.jpg"));
        Assert.Null(ConfinedHeroImage.TryOpen(tree.Root + "/../" + Path.GetFileName(tree.Root), file));
        Assert.Null(ConfinedHeroImage.TryOpen("/", file));
        Assert.Null(ConfinedHeroImage.TryOpen(tree.Root + "/missing", file));
    }

    [Fact]
    public void RejectsFinalAndIntermediateSymlinksAndNonregularFiles()
    {
        using var tree = new Tree();
        var file = tree.Make("inside/image.jpg");
        File.WriteAllBytes(file, Jpeg);
        var link = Path.Combine(tree.Root, "alias.jpg");
        File.CreateSymbolicLink(link, file);
        Assert.Null(ConfinedHeroImage.TryOpen(tree.Root, link));
        var external = tree.Root + "-external";
        Directory.CreateDirectory(external);
        try
        {
            File.WriteAllBytes(Path.Combine(external, "image.jpg"), Jpeg);
            Directory.CreateSymbolicLink(Path.Combine(tree.Root, "shortcut"), external);
            Assert.Null(ConfinedHeroImage.TryOpen(tree.Root, Path.Combine(tree.Root, "shortcut/image.jpg")));
        }
        finally { Directory.Delete(external, true); }
        Assert.Null(ConfinedHeroImage.TryOpen(tree.Root, Path.Combine(tree.Root, "inside")));
        var rootlink = tree.Root + "-link";
        Directory.CreateSymbolicLink(rootlink, tree.Root);
        try { Assert.Null(ConfinedHeroImage.TryOpen(rootlink, Path.Combine(rootlink, "inside/image.jpg"))); }
        finally { Directory.Delete(rootlink); }
    }

    [Fact]
    public async Task OpenHandleCannotBeRedirectedByRenameOrSymlinkReplacement()
    {
        using var tree = new Tree();
        var first = tree.Make("inside/image.jpg");
        File.WriteAllBytes(first, Jpeg);
        var outside = tree.Root + "-outside.jpg";
        File.WriteAllBytes(outside, [1, 2, 3]);
        try
        {
            await using var opened = ConfinedHeroImage.TryOpen(tree.Root, first);
            Assert.NotNull(opened);
            File.Move(first, first + ".moved");
            File.CreateSymbolicLink(first, outside);
            Assert.Equal(Jpeg, await HeroImageBounds.TryReadJpeg(opened, CancellationToken.None));
            Assert.Null(ConfinedHeroImage.TryOpen(tree.Root, first));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task ConcurrentRenameSwapsNeverReadSymlinkTarget()
    {
        using var tree = new Tree();
        var victim = tree.Make("inside/image.jpg");
        var outside = tree.Root + "-outside.jpg";
        File.WriteAllBytes(outside, [9, 8, 7, 6]);
        try
        {
            File.WriteAllBytes(victim, Jpeg);
            var alias = tree.Make("inside/alias.jpg");
            File.CreateSymbolicLink(alias, outside);
            using var stop = new CancellationTokenSource();
            var swapping = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    File.Move(victim, victim + ".bak", true);
                    File.Move(alias, victim, true);
                    File.Move(victim, alias, true);
                    File.Move(victim + ".bak", victim, true);
                }
            });
            try
            {
                for (var i = 0; i < 1000; i++)
                {
                    await using var opened = ConfinedHeroImage.TryOpen(tree.Root, victim);
                    if (opened is not null) Assert.Equal(Jpeg, await HeroImageBounds.TryReadJpeg(opened, CancellationToken.None));
                }
            }
            finally { stop.Cancel(); await swapping; }
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void RejectsCrossingExistingMountInsideRoot()
    {
        // /dev/shm is normally a nested tmpfs under /dev: no mount privileges required.
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm")) return;
        var file = "/dev/shm/rowan-confined-" + Guid.NewGuid().ToString("N") + ".jpg";
        try
        {
            try { File.WriteAllBytes(file, Jpeg); }
            catch (UnauthorizedAccessException) { return; }
            if (ConfinedHeroImage.TryOpen("/dev/shm", file) is not { } sameMount)
                return;
            sameMount.Dispose();
            using var crossed = ConfinedHeroImage.TryOpen("/dev", file);
            Assert.Null(crossed);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void RejectsOversizeFileAtOpenedHandle()
    {
        using var tree = new Tree();
        var file = tree.Make("large.jpg");
        using (var writer = File.Create(file)) writer.SetLength(HeroImageBounds.MaxBytes + 1);
        Assert.Null(ConfinedHeroImage.TryOpen(tree.Root, file));
    }

    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xc0, 0, 0x11, 8, 1, 0xe0, 2, 0x80, 3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 0xff, 0xd9];
    private sealed class Tree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rowan-confined-" + Guid.NewGuid().ToString("N"));
        public Tree() => Directory.CreateDirectory(Root);
        public string Make(string relative)
        {
            var full = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            return full;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
