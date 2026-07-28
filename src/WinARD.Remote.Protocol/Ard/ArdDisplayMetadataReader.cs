using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Ard;

internal static class ArdDisplayMetadataReader
{
    private const int DisplayInfoHeaderBytes = 8;
    private const int DisplayInfoRecordBytes = 28;
    private const int DisplayInfo2SizeBytes = 2;
    private const int SkipBufferBytes = 4096;

    internal static async ValueTask<ArdDisplaySize> ReadDisplayInfoAsync(
        RfbReader reader,
        Action<int> reserveBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(reserveBytes);

        reserveBytes(DisplayInfoHeaderBytes);
        var width = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        var height = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        var displayCount = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);

        int displayBytes;
        try
        {
            displayBytes = checked(displayCount * DisplayInfoRecordBytes);
        }
        catch (OverflowException exception)
        {
            throw new RfbProtocolException("Apple Remote Desktop display metadata length overflowed.", exception);
        }

        reserveBytes(displayBytes);
        await SkipAsync(reader, displayBytes, cancellationToken).ConfigureAwait(false);
        return new ArdDisplaySize(width, height);
    }

    internal static async ValueTask ReadDisplayInfo2Async(
        RfbReader reader,
        Action<int> reserveBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(reserveBytes);

        reserveBytes(DisplayInfo2SizeBytes);
        var payloadSize = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        reserveBytes(payloadSize);
        await SkipAsync(reader, payloadSize, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SkipAsync(
        RfbReader reader,
        int byteCount,
        CancellationToken cancellationToken)
    {
        var remaining = byteCount;
        while (remaining > 0)
        {
            var chunkLength = Math.Min(remaining, SkipBufferBytes);
            var chunk = await reader.ReadFramebufferPayloadBytesAsync(chunkLength, cancellationToken)
                .ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(chunk);
            remaining -= chunkLength;
        }
    }
}
