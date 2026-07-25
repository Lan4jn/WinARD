using System.Text;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Initialization;

public static class RfbSessionInitializer
{
    private const int MaximumDisplayNameCharacters = 4096;
    private static readonly int[] RequestedEncodings =
    [
        (int)RfbEncodingType.Zrle,
        (int)RfbEncodingType.Raw,
        (int)RfbEncodingType.CopyRect,
        (int)RfbEncodingType.Cursor,
        (int)RfbEncodingType.DesktopSize,
    ];

    /// <summary>
    /// Sends ClientInit, consumes ServerInit, and declares WinARD's pixel format and encodings.
    /// The write-through stream is neither flushed nor disposed.
    /// </summary>
    public static async Task<RfbServerInit> InitializeAsync(
        Stream stream,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();

        var reader = new RfbReader(stream, limits);
        var writer = new RfbWriter(stream);
        await writer.WriteByteAsync(1, cancellationToken);

        var width = await reader.ReadUInt16Async(cancellationToken);
        var height = await reader.ReadUInt16Async(cancellationToken);
        if (width == 0 || height == 0)
        {
            throw new RfbProtocolException("RFB ServerInit dimensions must be non-zero.");
        }

        var serverPixelFormat = PixelFormat.Parse(await reader.ReadBytesAsync(16, cancellationToken));
        var nameLength = await reader.ReadUInt32Async(cancellationToken);
        if (nameLength > limits.MaxMessageBytes)
        {
            throw new RfbProtocolException(
                $"RFB server name length {nameLength} exceeds the configured limit of {limits.MaxMessageBytes} bytes.");
        }

        var nameBytes = await reader.ReadBytesAsync(checked((int)nameLength), cancellationToken);
        var (name, isTruncated) = CreateDisplayName(nameBytes);

        await WriteSetPixelFormatAsync(writer, cancellationToken);
        await WriteSetEncodingsAsync(writer, cancellationToken);
        return new RfbServerInit(width, height, serverPixelFormat, name, isTruncated);
    }

    public static async Task WriteFramebufferUpdateRequestAsync(
        Stream stream,
        bool incremental,
        ushort x,
        ushort y,
        ushort width,
        ushort height,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (width == 0 || height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Framebuffer update request dimensions must be non-zero.");
        }

        var writer = new RfbWriter(stream);
        await writer.WriteByteAsync(3, cancellationToken);
        await writer.WriteByteAsync(incremental ? (byte)1 : (byte)0, cancellationToken);
        await writer.WriteUInt16Async(x, cancellationToken);
        await writer.WriteUInt16Async(y, cancellationToken);
        await writer.WriteUInt16Async(width, cancellationToken);
        await writer.WriteUInt16Async(height, cancellationToken);
    }

    private static async Task WriteSetPixelFormatAsync(RfbWriter writer, CancellationToken cancellationToken)
    {
        await writer.WriteBytesAsync(new byte[] { 0, 0, 0, 0 }, cancellationToken);
        await writer.WriteBytesAsync(PixelFormat.WinArdBgra32.ToWireBytes(), cancellationToken);
    }

    private static async Task WriteSetEncodingsAsync(RfbWriter writer, CancellationToken cancellationToken)
    {
        await writer.WriteBytesAsync(new byte[] { 2, 0 }, cancellationToken);
        await writer.WriteUInt16Async(checked((ushort)RequestedEncodings.Length), cancellationToken);
        foreach (var encoding in RequestedEncodings)
        {
            await writer.WriteInt32Async(encoding, cancellationToken);
        }
    }

    private static (string Name, bool IsTruncated) CreateDisplayName(ReadOnlySpan<byte> bytes)
    {
        var decoded = Encoding.UTF8.GetString(bytes);
        var characters = decoded.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (char.IsControl(characters[index]) || characters[index] is '\u2028' or '\u2029')
            {
                characters[index] = ' ';
            }
        }

        var cleaned = new string(characters);
        if (cleaned.Length <= MaximumDisplayNameCharacters)
        {
            return (cleaned, false);
        }

        var displayLength = MaximumDisplayNameCharacters;
        if (char.IsHighSurrogate(cleaned[displayLength - 1]) && char.IsLowSurrogate(cleaned[displayLength]))
        {
            displayLength--;
        }

        return (cleaned[..displayLength], true);
    }
}
