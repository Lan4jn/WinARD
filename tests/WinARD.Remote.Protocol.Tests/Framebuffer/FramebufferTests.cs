using System.Reflection;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using Xunit;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Framebuffer;

public sealed class FramebufferTests
{
    [Fact]
    public void Construction_sets_stride_and_opaque_black_pixels()
    {
        using var framebuffer = new FramebufferModel(4, 4, ProtocolLimits.Default);

        Assert.Equal(4, framebuffer.Width);
        Assert.Equal(4, framebuffer.Height);
        Assert.Equal(16, framebuffer.Stride);
        Assert.Equal(0xFF000000u, framebuffer.GetBgra32(0, 0));
        Assert.Equal(0xFF000000u, framebuffer.GetBgra32(3, 3));
    }

    [Fact]
    public void Pixel_snapshot_is_defensive()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        var snapshot = framebuffer.GetPixelsBgra32();
        snapshot[3] = 0;

        Assert.Equal(0xFF000000u, framebuffer.GetBgra32(0, 0));
    }

    [Fact]
    public void ApplyRaw_forces_opaque_alpha()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        framebuffer.ApplyRaw(new FramebufferRect(0, 0, 1, 1), [1, 2, 3, 0]);

        Assert.Equal(0xFF030201u, framebuffer.GetBgra32(0, 0));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void Get_pixel_rejects_out_of_bounds_coordinates(int x, int y)
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);

        Assert.Throws<ArgumentOutOfRangeException>(() => framebuffer.GetBgra32(x, y));
    }

    [Fact]
    public void Construction_rejects_framebuffer_above_limit_before_allocation()
    {
        Assert.Throws<RfbProtocolException>(() =>
            new FramebufferModel(int.MaxValue, int.MaxValue, new ProtocolLimits(1024, 1024)));
    }

    [Fact]
    public void Failed_resize_preserves_previous_dimensions_and_pixels()
    {
        using var framebuffer = new FramebufferModel(1, 1, new ProtocolLimits(1024, 16));
        framebuffer.ApplyRaw(new FramebufferRect(0, 0, 1, 1), [1, 2, 3, 255]);

        Assert.Throws<RfbProtocolException>(() => framebuffer.Resize(3, 3));

        Assert.Equal(1, framebuffer.Width);
        Assert.Equal(1, framebuffer.Height);
        Assert.Equal(0xFF030201u, framebuffer.GetBgra32(0, 0));
    }

    [Fact]
    public void Successful_resize_replaces_with_opaque_black_buffer()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        framebuffer.ApplyRaw(new FramebufferRect(0, 0, 1, 1), [1, 2, 3, 255]);

        framebuffer.Resize(2, 2);

        Assert.Equal(2, framebuffer.Width);
        Assert.Equal(2, framebuffer.Height);
        Assert.Equal(0xFF000000u, framebuffer.GetBgra32(1, 1));
    }

    [Fact]
    public void CopyRect_handles_downward_overlap_like_memmove()
    {
        using var framebuffer = Row(1, 2, 3, 4);

        framebuffer.CopyRect(new FramebufferRect(0, 1, 1, 3), 0, 0);

        Assert.Equal([1u, 1u, 2u, 3u], BlueValues(framebuffer));
    }

    [Fact]
    public void CopyRect_handles_upward_overlap_like_memmove()
    {
        using var framebuffer = Row(1, 2, 3, 4);

        framebuffer.CopyRect(new FramebufferRect(0, 0, 1, 3), 0, 1);

        Assert.Equal([2u, 3u, 4u, 4u], BlueValues(framebuffer));
    }

    [Fact]
    public void CopyRect_handles_horizontal_overlap_like_memmove()
    {
        using var framebuffer = Column(1, 2, 3, 4);

        framebuffer.CopyRect(new FramebufferRect(1, 0, 3, 1), 0, 0);

        Assert.Equal([1u, 1u, 2u, 3u], BlueValues(framebuffer));
    }

    [Fact]
    public void CopyRect_handles_leftward_overlap_like_memmove()
    {
        using var framebuffer = Column(1, 2, 3, 4);

        framebuffer.CopyRect(new FramebufferRect(0, 0, 3, 1), 1, 0);

        Assert.Equal([2u, 3u, 4u, 4u], BlueValues(framebuffer));
    }

    [Fact]
    public void Invalid_copy_does_not_modify_pixels()
    {
        using var framebuffer = Column(1, 2, 3, 4);
        var before = framebuffer.GetPixelsBgra32();

        Assert.Throws<RfbProtocolException>(() =>
            framebuffer.CopyRect(new FramebufferRect(0, 0, 2, 1), 3, 0));

        Assert.Equal(before, framebuffer.GetPixelsBgra32());
    }

    [Fact]
    public void Dispose_zeroes_storage_and_rejects_operations()
    {
        var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        framebuffer.ApplyRaw(new FramebufferRect(0, 0, 1, 1), [1, 2, 3, 255]);
        var field = typeof(FramebufferModel).GetField("_pixels", BindingFlags.Instance | BindingFlags.NonPublic);
        var storage = Assert.IsType<byte[]>(field!.GetValue(framebuffer));

        framebuffer.Dispose();

        Assert.All(storage, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => framebuffer.GetBgra32(0, 0));
        Assert.Throws<ObjectDisposedException>(() => framebuffer.Resize(1, 1));
    }

    [Theory]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    public void Rectangle_rejects_empty_size(int x, int y, int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FramebufferRect(x, y, width, height));
    }

    private static FramebufferModel Row(params byte[] values)
    {
        var framebuffer = new FramebufferModel(1, values.Length, ProtocolLimits.Default);
        framebuffer.ApplyRaw(new FramebufferRect(0, 0, 1, values.Length), Pixels(values));
        return framebuffer;
    }

    private static FramebufferModel Column(params byte[] values)
    {
        var framebuffer = new FramebufferModel(values.Length, 1, ProtocolLimits.Default);
        framebuffer.ApplyRaw(new FramebufferRect(0, 0, values.Length, 1), Pixels(values));
        return framebuffer;
    }

    private static byte[] Pixels(byte[] blueValues)
    {
        var pixels = new byte[blueValues.Length * 4];
        for (var index = 0; index < blueValues.Length; index++)
        {
            pixels[index * 4] = blueValues[index];
            pixels[(index * 4) + 3] = 255;
        }

        return pixels;
    }

    private static uint[] BlueValues(FramebufferModel framebuffer)
    {
        var values = new uint[framebuffer.Width * framebuffer.Height];
        for (var y = 0; y < framebuffer.Height; y++)
        {
            for (var x = 0; x < framebuffer.Width; x++)
            {
                values[(y * framebuffer.Width) + x] = framebuffer.GetBgra32(x, y) & 0xFF;
            }
        }

        return values;
    }
}
