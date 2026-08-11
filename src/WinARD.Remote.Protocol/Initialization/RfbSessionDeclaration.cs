using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Framebuffer;

namespace WinARD.Remote.Protocol.Initialization;

public sealed record RfbSessionDeclaration
{
    public RfbSessionDeclaration(PixelFormat pixelFormat, IReadOnlyList<int> encodings)
    {
        ArgumentNullException.ThrowIfNull(pixelFormat);
        ArgumentNullException.ThrowIfNull(encodings);
        if (encodings.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encodings),
                "An RFB session declaration cannot contain more than 65535 encodings.");
        }

        PixelFormat = pixelFormat;
        Encodings = Array.AsReadOnly(encodings.ToArray());
    }

    public static RfbSessionDeclaration Default { get; } = new(
        PixelFormat.WinArdBgra32,
        [
            (int)RfbEncodingType.Zrle,
            (int)RfbEncodingType.Raw,
            (int)RfbEncodingType.CopyRect,
            (int)RfbEncodingType.Cursor,
            (int)RfbEncodingType.DesktopSize,
        ]);

    public PixelFormat PixelFormat { get; }

    public IReadOnlyList<int> Encodings { get; }
}
