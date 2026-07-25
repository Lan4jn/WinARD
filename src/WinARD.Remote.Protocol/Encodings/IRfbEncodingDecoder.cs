using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

namespace WinARD.Remote.Protocol.Encodings;

public interface IRfbEncodingDecoder
{
    int EncodingId { get; }

    ValueTask<EncodingDecodeResult> DecodeAsync(
        RfbReader reader,
        FramebufferModel framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken);
}
