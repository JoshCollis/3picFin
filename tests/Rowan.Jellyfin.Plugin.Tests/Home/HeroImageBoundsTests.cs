using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Rowan.Jellyfin.Plugin.Home;
using Xunit;

namespace Rowan.Jellyfin.Plugin.Tests.Home;

public sealed class HeroImageBoundsTests
{
    private static byte[] Jpeg(int width, int height) => [
        0xff, 0xd8, 0xff, 0xc0, 0, 0x11, 8, (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width, 3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0,
        0xff, 0xd9];

    [Fact]
    public async Task RejectsOversizeAndPixelBombBeforeDecoding()
    {
        Assert.Null(await HeroImageBounds.TryReadJpeg(new MemoryStream(Jpeg(5000, 5000)), CancellationToken.None));
        Assert.Null(await HeroImageBounds.TryReadJpeg(new MemoryStream(new byte[HeroImageBounds.MaxBytes + 1]), CancellationToken.None));
        Assert.NotNull(await HeroImageBounds.TryReadJpeg(new MemoryStream(Jpeg(1920, 1080)), CancellationToken.None));
    }

    [Fact]
    public async Task RejectsNonJpegAndTruncatedOrFakeJpeg()
    {
        Assert.Null(await HeroImageBounds.TryReadJpeg(new MemoryStream([0xff, 0xd8, 0xff, 0xd9]), CancellationToken.None));
        Assert.Null(await HeroImageBounds.TryReadJpeg(new MemoryStream([0xff, 0xd8, 0xff, 0xc0, 0, 0x11]), CancellationToken.None));
        Assert.Null(await HeroImageBounds.TryReadJpeg(new MemoryStream([0x89, 0x50, 0x4e, 0x47]), CancellationToken.None));
    }

    [Fact]
    public void LimitsGlobalAndPerUserWithoutGrowingUserMap()
    {
        // Keep the rate window fixed, and use different slots so hash collisions
        // do not turn the per-user assertion into a collision-policy assertion.
        var gate = new HeroImageGate(2, () => 1_000_000);
        var a = Guid.Empty;
        var b = new Guid(1, 0, 0, new byte[8]);
        Assert.NotEqual((uint)a.GetHashCode() % 256, (uint)b.GetHashCode() % 256);
        using var first = gate.TryEnter(a);
        Assert.NotNull(first);
        Assert.Null(gate.TryEnter(a));
        using var second = gate.TryEnter(b);
        Assert.NotNull(second);
        Assert.Null(gate.TryEnter(Guid.NewGuid()));
        Assert.Equal(2, gate.Active);
        first.Dispose();
        using var reentered = gate.TryEnter(a);
        Assert.NotNull(reentered);
        Assert.Equal(2, gate.Active);
        reentered.Dispose();
        Assert.Equal(1, gate.Active);
        second.Dispose();
        Assert.Equal(0, gate.Active);
    }

    [Fact]
    public void BoundedContentionDoesNotExceedCapacityOrRetainUsers()
    {
        long tick = 1_000_000;
        var gate = new HeroImageGate(4, () => Volatile.Read(ref tick));
        var admitted = 0;
        System.Threading.Tasks.Parallel.For(0, 10_000, i => {
            using var lease = gate.TryEnter(Guid.NewGuid());
            if (lease is null) return;
            Interlocked.Increment(ref admitted);
            Assert.InRange(gate.Active, 0, 4);
        });
        Assert.InRange(admitted, 1, 16);
        Assert.Equal(0, gate.Active);
        Volatile.Write(ref tick, 1_001_000);
        using var nextWindow = gate.TryEnter(Guid.NewGuid());
        Assert.NotNull(nextWindow);
        Assert.Equal(1, gate.Active);
    }

    [Fact]
    public void PerUserLimitResetsOnlyWhenWindowAdvances()
    {
        long tick = 1_000_000;
        var gate = new HeroImageGate(4, () => Volatile.Read(ref tick));
        var user = Guid.NewGuid();
        for (var i = 0; i < 4; i++)
        {
            using var lease = gate.TryEnter(user);
            Assert.NotNull(lease);
        }
        Assert.Null(gate.TryEnter(user));
        Assert.Equal(0, gate.Active);
        Volatile.Write(ref tick, 1_001_000);
        using var nextWindow = gate.TryEnter(user);
        Assert.NotNull(nextWindow);
    }

}
