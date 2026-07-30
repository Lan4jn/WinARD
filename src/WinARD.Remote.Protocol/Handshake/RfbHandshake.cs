using System.Text;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Handshake;

public static class RfbHandshake
{
    private const int VersionBannerLength = 12;
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
        byte[] banner;
        try
        {
            banner = await reader.ReadBytesAsync(VersionBannerLength, cancellationToken).ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(HandshakeFailureInfo(RfbHandshakeStage.VersionBanner));
        }

        RfbVersion version;
        try
        {
            version = RfbVersion.Parse(banner);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(HandshakeFailureInfo(
                RfbHandshakeStage.VersionParse,
                VersionBannerLength,
                VersionBannerLength));
        }

        await writer.WriteMessageAsync(Encoding.ASCII.GetBytes(version.Banner), cancellationToken)
            .ConfigureAwait(false);

        return version == RfbVersion.V3_3
            ? await NegotiateRfb33Async(reader, version, limits, cancellationToken).ConfigureAwait(false)
            : await NegotiateRfb37Or38Async(reader, writer, version, limits, cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task<RfbHandshakeResult> NegotiateRfb33Async(
        RfbReader reader,
        RfbVersion version,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        uint securityType;
        try
        {
            securityType = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(HandshakeFailureInfo(RfbHandshakeStage.SecurityType33));
        }
        if (securityType == (uint)RfbSecurityType.Invalid)
        {
            throw await ReadRejectionAsync(reader, version, limits, cancellationToken).ConfigureAwait(false);
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
        byte numberOfSecurityTypes;
        try
        {
            numberOfSecurityTypes = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(HandshakeFailureInfo(RfbHandshakeStage.SecurityTypeCount));
        }
        if (numberOfSecurityTypes == 0)
        {
            throw await ReadRejectionAsync(reader, version, limits, cancellationToken).ConfigureAwait(false);
        }

        byte[] offeredTypes;
        try
        {
            offeredTypes = await reader.ReadBytesAsync(numberOfSecurityTypes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(HandshakeFailureInfo(RfbHandshakeStage.SecurityTypes));
        }
        if (!offeredTypes.Contains((byte)RfbSecurityType.AppleRemoteDesktop))
        {
            throw new UnsupportedSecurityTypeException(version, offeredTypes.Select(value => (uint)value));
        }

        await writer.WriteByteAsync((byte)RfbSecurityType.AppleRemoteDesktop, cancellationToken)
            .ConfigureAwait(false);
        return new RfbHandshakeResult(version, RfbSecurityType.AppleRemoteDesktop);
    }

    private static async Task<RfbConnectionRejectedException> ReadRejectionAsync(
        RfbReader reader,
        RfbVersion version,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        var failure = await RfbFailureReasonReader.ReadAsync(reader, limits, cancellationToken)
            .ConfigureAwait(false);
        return new RfbConnectionRejectedException(version, failure.Reason, failure.IsTruncated);
    }

    private static RfbProtocolFailureInfo HandshakeFailureInfo(
        RfbHandshakeStage stage,
        int? expectedByteCount = null,
        int? actualByteCount = null) =>
        new(
            RfbProtocolFailureKind.TruncatedRead,
            HandshakeStage: stage,
            ExpectedByteCount: expectedByteCount,
            ActualByteCount: actualByteCount);
}
