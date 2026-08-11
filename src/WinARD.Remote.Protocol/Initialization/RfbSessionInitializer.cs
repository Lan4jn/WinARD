using System.Buffers.Binary;
using System.Text;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Initialization;

public static class RfbSessionInitializer
{
    private const int MaximumDisplayNameCharacters = 4096;
    private static readonly int[] ArdBootstrapRequestedEncodings =
    [
        (int)RfbEncodingType.ArdDisplayInfo,
        (int)RfbEncodingType.ArdDisplayInfo2,
        (int)RfbEncodingType.DesktopSize,
    ];
    /// <summary>
    /// Sends ClientInit, consumes ServerInit, and declares WinARD's pixel format and encodings.
    /// The write-through stream is neither flushed nor disposed.
    /// </summary>
    public static Task<RfbServerInit> InitializeAsync(
        Stream stream,
        RfbHandshakeResult handshake,
        ProtocolLimits limits,
        CancellationToken cancellationToken) =>
        InitializeAsync(
            stream,
            handshake,
            limits,
            RfbSessionDeclaration.Default,
            null,
            cancellationToken);

    public static Task<RfbServerInit> InitializeAsync(
        Stream stream,
        RfbHandshakeResult handshake,
        RfbSessionDeclaration declaration,
        ProtocolLimits limits,
        CancellationToken cancellationToken) =>
        InitializeAsync(stream, handshake, limits, declaration, null, cancellationToken);

    public static Task<RfbServerInit> InitializeAsync(
        Stream stream,
        RfbHandshakeResult handshake,
        ProtocolLimits limits,
        ArdSessionEncryption? sessionEncryption,
        CancellationToken cancellationToken) =>
        InitializeAsync(
            stream,
            handshake,
            limits,
            RfbSessionDeclaration.Default,
            sessionEncryption,
            cancellationToken);

    public static async Task<RfbServerInit> InitializeAsync(
        Stream stream,
        RfbHandshakeResult handshake,
        ProtocolLimits limits,
        RfbSessionDeclaration declaration,
        ArdSessionEncryption? sessionEncryption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(handshake);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(declaration);
        cancellationToken.ThrowIfCancellationRequested();
        if (handshake.SecurityType != RfbSecurityType.AppleRemoteDesktop)
        {
            throw new ArgumentException(
                "RFB session initialization requires Apple Remote Desktop security.",
                nameof(handshake));
        }

        var ardEncodings = handshake.Version == RfbVersion.V3_889
            ? BuildArdRequestedEncodings(declaration, sessionEncryption)
            : null;
        var reader = new RfbReader(stream, limits);
        var writer = new RfbWriter(stream);
        if (handshake.Version == RfbVersion.V3_889)
        {
            return await InitializeArdAsync(
                    stream,
                    reader,
                    writer,
                    limits,
                    declaration,
                    ardEncodings!,
                    sessionEncryption,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (sessionEncryption is not null)
        {
            throw new ArgumentException(
                "ARD session encryption is supported only by the Apple 3.889 protocol path.",
                nameof(sessionEncryption));
        }

        return await InitializeStandardAsync(reader, writer, limits, declaration, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<RfbServerInit> InitializeStandardAsync(
        RfbReader reader,
        RfbWriter writer,
        ProtocolLimits limits,
        RfbSessionDeclaration declaration,
        CancellationToken cancellationToken)
    {
        await writer.WriteByteAsync(1, cancellationToken).ConfigureAwait(false);

        var width = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        var height = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        if (width == 0 || height == 0)
        {
            throw new RfbProtocolException("RFB ServerInit dimensions must be non-zero.");
        }

        var serverPixelFormat = PixelFormat.Parse(
            await reader.ReadBytesAsync(16, cancellationToken).ConfigureAwait(false));
        var nameLength = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
        if (nameLength > limits.MaxMessageBytes)
        {
            throw new RfbProtocolException(
                $"RFB server name length {nameLength} exceeds the configured limit of {limits.MaxMessageBytes} bytes.");
        }

        var nameBytes = await reader.ReadBytesAsync(checked((int)nameLength), cancellationToken)
            .ConfigureAwait(false);
        var (name, isTruncated) = CreateDisplayName(nameBytes);

        await WriteSetPixelFormatAsync(writer, declaration.PixelFormat, cancellationToken).ConfigureAwait(false);
        await WriteSetEncodingsAsync(writer, declaration.Encodings, cancellationToken).ConfigureAwait(false);
        return new RfbServerInit(width, height, serverPixelFormat, name, isTruncated);
    }

    private static async Task<RfbServerInit> InitializeArdAsync(
        Stream stream,
        RfbReader reader,
        RfbWriter writer,
        ProtocolLimits limits,
        RfbSessionDeclaration declaration,
        IReadOnlyList<int> ardEncodings,
        ArdSessionEncryption? sessionEncryption,
        CancellationToken cancellationToken)
    {
        await writer.WriteByteAsync((byte)ArdClientInitFlags.Ard, cancellationToken).ConfigureAwait(false);

        var width = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        var height = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        var serverPixelFormat = PixelFormat.Parse(
            await reader.ReadBytesAsync(16, cancellationToken).ConfigureAwait(false));
        var nameLength = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
        if (nameLength > limits.MaxMessageBytes)
        {
            throw new RfbProtocolException(
                $"RFB server name length {nameLength} exceeds the configured limit of {limits.MaxMessageBytes} bytes.");
        }

        var nameBytes = await reader.ReadBytesAsync(checked((int)nameLength), cancellationToken)
            .ConfigureAwait(false);
        var extendedInitialization = ArdServerInitParser.Parse(nameBytes);
        var capabilities = extendedInitialization.Capabilities;
        var (name, isTruncated) = CreateDisplayName(extendedInitialization.DisplayNameBytes.Span);
        var hasZeroDimension = width == 0 || height == 0;
        var requiresBootstrap = width == 0 && height == 0;

        if (hasZeroDimension &&
            (!capabilities.RequiresSessionSelection || !requiresBootstrap))
        {
            throw new RfbProtocolException(
                "Apple Remote Desktop ServerInit dimensions must both be non-zero, or both be zero with session selection.");
        }

        if (capabilities.RequiresSessionSelection)
        {
            await ArdSessionSelector.SelectConsoleAsync(stream, limits, cancellationToken).ConfigureAwait(false);
        }

        var ardWriter = new ArdClientMessageWriter(writer);
        await ardWriter.WriteViewerInfoAsync(cancellationToken).ConfigureAwait(false);
        await ardWriter.WriteSetModeAsync(ArdControlMode.Shared, cancellationToken).ConfigureAwait(false);
        await ardWriter.WriteSetDisplayAsync(cancellationToken).ConfigureAwait(false);
        await WriteSetPixelFormatAsync(writer, declaration.PixelFormat, cancellationToken).ConfigureAwait(false);
        await WriteSetEncodingsAsync(
                writer,
                requiresBootstrap
                    ? ArdBootstrapRequestedEncodings
                    : ardEncodings,
                cancellationToken)
            .ConfigureAwait(false);

        if (requiresBootstrap)
        {
            var displaySize = await ArdDisplayBootstrapReader.ReadAsync(stream, limits, cancellationToken)
                .ConfigureAwait(false);
            width = displaySize.Width;
            height = displaySize.Height;
            await WriteSetEncodingsAsync(
                    writer,
                    ardEncodings,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (sessionEncryption is not null)
        {
            await sessionEncryption.RequestAsync(cancellationToken).ConfigureAwait(false);
        }

        return new RfbServerInit(width, height, serverPixelFormat, name, isTruncated)
        {
            ArdCapabilities = capabilities,
        };
    }

    private static List<int> BuildArdRequestedEncodings(
        RfbSessionDeclaration declaration,
        ArdSessionEncryption? sessionEncryption)
    {
        var encodings = declaration.Encodings.ToList();
        AddIfMissing(encodings, (int)RfbEncodingType.ArdDisplayInfo);
        AddIfMissing(encodings, (int)RfbEncodingType.ArdDisplayInfo2);
        AddIfMissing(encodings, (int)RfbEncodingType.DesktopSize);
        if (sessionEncryption is not null)
        {
            AddIfMissing(encodings, (int)RfbEncodingType.ArdSessionEncryption);
        }

        ValidateEncodingCount(encodings);

        return encodings;
    }

    private static void ValidateEncodingCount(List<int> encodings)
    {
        if (encodings.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encodings),
                "An RFB SetEncodings message cannot contain more than 65535 entries.");
        }
    }

    private static void AddIfMissing(List<int> encodings, int encoding)
    {
        if (!encodings.Contains(encoding))
        {
            encodings.Add(encoding);
        }
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

        var message = new byte[10];
        message[0] = 3;
        message[1] = incremental ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), x);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), y);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6), width);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), height);
        await new RfbWriter(stream).WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public static Task WriteSetPixelFormatAsync(
        Stream stream,
        PixelFormat pixelFormat,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(pixelFormat);
        cancellationToken.ThrowIfCancellationRequested();
        return WriteSetPixelFormatAsync(new RfbWriter(stream), pixelFormat, cancellationToken);
    }

    private static async Task WriteSetPixelFormatAsync(
        RfbWriter writer,
        PixelFormat pixelFormat,
        CancellationToken cancellationToken)
    {
        var message = new byte[20];
        pixelFormat.ToWireBytes().CopyTo(message, 4);
        await writer.WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public static ValueTask WriteSetEncodingsAsync(
        Stream stream,
        IReadOnlyList<int> encodings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(encodings);
        cancellationToken.ThrowIfCancellationRequested();
        return new RfbWriter(stream).WriteMessageAsync(
            BuildSetEncodingsMessage(encodings),
            cancellationToken);
    }

    private static async Task WriteSetEncodingsAsync(
        RfbWriter writer,
        IReadOnlyList<int> encodings,
        CancellationToken cancellationToken)
    {
        await writer.WriteMessageAsync(BuildSetEncodingsMessage(encodings), cancellationToken)
            .ConfigureAwait(false);
    }

    private static byte[] BuildSetEncodingsMessage(IReadOnlyList<int> encodings)
    {
        ArgumentNullException.ThrowIfNull(encodings);
        if (encodings.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encodings),
                "An RFB SetEncodings message cannot contain more than 65535 entries.");
        }

        var message = new byte[checked(4 + (encodings.Count * sizeof(int)))];
        message[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(
            message.AsSpan(2),
            checked((ushort)encodings.Count));
        for (var index = 0; index < encodings.Count; index++)
        {
            BinaryPrimitives.WriteInt32BigEndian(
                message.AsSpan(4 + (index * sizeof(int))),
                encodings[index]);
        }

        return message;
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
