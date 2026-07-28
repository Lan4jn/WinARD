using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Encodings;

internal sealed class ArdDisplayInfoEncoding : IRfbEncodingDecoder
{
    public int EncodingId => (int)RfbEncodingType.ArdDisplayInfo;

    public async ValueTask<EncodingDecodeResult> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        var displaySize = await ArdDisplayMetadataReader.ReadDisplayInfoAsync(
            reader,
            byteCount => ReserveMetadataBytes(reader, byteCount),
            cancellationToken).ConfigureAwait(false);
        if (displaySize.Width != 0 &&
            displaySize.Height != 0 &&
            (displaySize.Width != framebuffer.Width || displaySize.Height != framebuffer.Height))
        {
            DesktopSizeEncoding.ResizeFramebuffer(reader, framebuffer, displaySize.Width, displaySize.Height);
        }

        return EncodingDecodeResult.Empty;
    }

    private static void ReserveMetadataBytes(RfbReader reader, int byteCount)
    {
        reader.ReserveFramebufferUpdateBytes(byteCount);
        reader.ReserveFramebufferUpdateWorkBytes(byteCount);
    }
}

internal sealed class ArdDisplayInfo2Encoding : IRfbEncodingDecoder
{
    public int EncodingId => (int)RfbEncodingType.ArdDisplayInfo2;

    public async ValueTask<EncodingDecodeResult> DecodeAsync(
        RfbReader reader,
        Framebuffer.Framebuffer framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        await ArdDisplayMetadataReader.ReadDisplayInfo2Async(
            reader,
            byteCount => ReserveMetadataBytes(reader, byteCount),
            cancellationToken).ConfigureAwait(false);
        if (rectangle.Width != 0 &&
            rectangle.Height != 0 &&
            (rectangle.Width != framebuffer.Width || rectangle.Height != framebuffer.Height))
        {
            DesktopSizeEncoding.ResizeFramebuffer(reader, framebuffer, rectangle.Width, rectangle.Height);
        }

        return EncodingDecodeResult.Empty;
    }

    private static void ReserveMetadataBytes(RfbReader reader, int byteCount)
    {
        reader.ReserveFramebufferUpdateBytes(byteCount);
        reader.ReserveFramebufferUpdateWorkBytes(byteCount);
    }
}
