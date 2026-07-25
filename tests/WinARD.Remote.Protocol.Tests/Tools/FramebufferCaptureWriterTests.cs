using System.Buffers.Binary;
using WinARD.ProtocolProbe;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using Xunit;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Tools;

public sealed class FramebufferCaptureWriterTests
{
    [Fact]
    public async Task WriteBmp_includes_dimensions_and_bottom_up_bgra_pixels()
    {
        using var framebuffer = new FramebufferModel(1, 2, ProtocolLimits.Default);
        framebuffer.ApplyRaw(
            new FramebufferRect(0, 0, 1, 2),
            [1, 2, 3, 255, 4, 5, 6, 255]);
        var path = Path.Combine(Path.GetTempPath(), $"winard-{Guid.NewGuid():N}.bmp");
        try
        {
            await FramebufferCaptureWriter.WriteBmpAsync(path, framebuffer, CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal((byte)'B', bytes[0]);
            Assert.Equal((byte)'M', bytes[1]);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18)));
            Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22)));
            Assert.Equal((ushort)32, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)));
            Assert.Equal([4, 5, 6, 255, 1, 2, 3, 255], bytes[54..]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteBmp_does_not_overwrite_existing_file()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var path = Path.Combine(Path.GetTempPath(), $"winard-{Guid.NewGuid():N}.bmp");
        await File.WriteAllTextAsync(path, "keep");
        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                FramebufferCaptureWriter.WriteBmpAsync(path, framebuffer, CancellationToken.None));

            Assert.Equal("keep", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
