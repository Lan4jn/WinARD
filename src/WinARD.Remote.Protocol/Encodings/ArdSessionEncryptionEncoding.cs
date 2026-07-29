using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

namespace WinARD.Remote.Protocol.Encodings;

internal sealed class ArdSessionEncryptionEncoding(ArdSessionEncryption encryption) : IRfbEncodingDecoder
{
    private const int PayloadLength = 36;

    public int EncodingId => (int)RfbEncodingType.ArdSessionEncryption;

    public async ValueTask<EncodingDecodeResult> DecodeAsync(
        RfbReader reader,
        FramebufferModel framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(framebuffer);
        if (rectangle.X != 0 || rectangle.Y != 0 || rectangle.Width != 0 || rectangle.Height != 0)
        {
            throw NegotiationFailure("ARD session encryption rectangles must use zero coordinates and dimensions.");
        }

        reader.ReserveFramebufferUpdateBytes(PayloadLength);
        reader.ReserveFramebufferUpdateWorkBytes(PayloadLength);
        var payload = await reader.ReadFramebufferPayloadBytesAsync(PayloadLength, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var version = BinaryPrimitives.ReadUInt32BigEndian(payload);
            if (version != 1)
            {
                throw NegotiationFailure($"Unsupported ARD session encryption version {version}.");
            }

            encryption.AcceptSessionMaterial(payload.AsSpan(4, 16), payload.AsSpan(20, 16));
            return EncodingDecodeResult.Empty;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static RfbProtocolException NegotiationFailure(string message) =>
        RfbProtocolException.Create(
            message,
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.ArdEncryptionNegotiation));
}
