using System.Text;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Handshake;

public static class RfbHandshake
{
    private const int VersionBannerLength = 12;
    private const int MaximumDisplayedReasonLength = 4096;

    /// <summary>
    /// Negotiates the RFB version and Apple Remote Desktop security type over a write-through transport stream.
    /// This method never flushes or disposes <paramref name="stream" />.
    /// </summary>
    public static Task<RfbHandshakeResult> NegotiateAsync(Stream stream, CancellationToken cancellationToken) =>
        NegotiateAsync(stream, ProtocolLimits.Default, cancellationToken);

    /// <summary>
    /// Negotiates the RFB version and Apple Remote Desktop security type over a write-through transport stream.
    /// This method never flushes or disposes <paramref name="stream" />.
    /// </summary>
    public static async Task<RfbHandshakeResult> NegotiateAsync(
        Stream stream,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();

        var reader = new RfbReader(stream, limits);
        var writer = new RfbWriter(stream);
        var version = RfbVersion.Parse(await reader.ReadBytesAsync(VersionBannerLength, cancellationToken));
        await writer.WriteBytesAsync(Encoding.ASCII.GetBytes(version.Banner), cancellationToken);

        return version == RfbVersion.V3_3
            ? await NegotiateRfb33Async(reader, version, limits, cancellationToken)
            : await NegotiateRfb37Or38Async(reader, writer, version, limits, cancellationToken);
    }

    private static async Task<RfbHandshakeResult> NegotiateRfb33Async(
        RfbReader reader,
        RfbVersion version,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        var securityType = await reader.ReadUInt32Async(cancellationToken);
        if (securityType == (uint)RfbSecurityType.Invalid)
        {
            throw await ReadRejectionAsync(reader, version, limits, cancellationToken);
        }

        if (securityType != (uint)RfbSecurityType.AppleRemoteDesktop)
        {
            throw new UnsupportedSecurityTypeException(version, [securityType]);
        }

        return new RfbHandshakeResult(version, RfbSecurityType.AppleRemoteDesktop);
    }

    private static async Task<RfbHandshakeResult> NegotiateRfb37Or38Async(
        RfbReader reader,
        RfbWriter writer,
        RfbVersion version,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        var numberOfSecurityTypes = await reader.ReadByteAsync(cancellationToken);
        if (numberOfSecurityTypes == 0)
        {
            throw await ReadRejectionAsync(reader, version, limits, cancellationToken);
        }

        var offeredTypes = await reader.ReadBytesAsync(numberOfSecurityTypes, cancellationToken);
        if (!offeredTypes.Contains((byte)RfbSecurityType.AppleRemoteDesktop))
        {
            throw new UnsupportedSecurityTypeException(version, offeredTypes.Select(value => (uint)value));
        }

        await writer.WriteByteAsync((byte)RfbSecurityType.AppleRemoteDesktop, cancellationToken);
        return new RfbHandshakeResult(version, RfbSecurityType.AppleRemoteDesktop);
    }

    private static async Task<RfbConnectionRejectedException> ReadRejectionAsync(
        RfbReader reader,
        RfbVersion version,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        var reasonLength = await reader.ReadUInt32Async(cancellationToken);
        if (reasonLength > (uint)limits.MaxMessageBytes)
        {
            throw new RfbProtocolException(
                $"Server rejection reason length {reasonLength} exceeds the configured limit of {limits.MaxMessageBytes} bytes.");
        }

        var reasonBytes = await reader.ReadBytesAsync((int)reasonLength, cancellationToken);
        var (reason, isReasonTruncated) = CreateSafeDisplayedReason(Encoding.UTF8.GetString(reasonBytes));

        return new RfbConnectionRejectedException(version, reason, isReasonTruncated);
    }

    private static (string Reason, bool IsTruncated) CreateSafeDisplayedReason(string decodedReason)
    {
        var builder = new StringBuilder(Math.Min(decodedReason.Length, MaximumDisplayedReasonLength));
        var runeCount = 0;
        foreach (var rune in decodedReason.EnumerateRunes())
        {
            if (runeCount == MaximumDisplayedReasonLength)
            {
                return (builder.ToString(), true);
            }

            if (IsUnsafeDisplayRune(rune))
            {
                builder.Append("\\u");
                builder.Append(rune.Value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(rune.ToString());
            }

            runeCount++;
        }

        return (builder.ToString(), false);
    }

    private static bool IsUnsafeDisplayRune(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is
            System.Globalization.UnicodeCategory.Control or
            System.Globalization.UnicodeCategory.Format or
            System.Globalization.UnicodeCategory.LineSeparator or
            System.Globalization.UnicodeCategory.ParagraphSeparator;
}
