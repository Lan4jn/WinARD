using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using WinARD.Remote.Protocol.Handshake;

namespace WinARD.ProtocolProbe.RdmCapture;

public sealed record ArdProbeServerHandshakeResult(
    RfbVersion Version,
    byte ClientInit,
    int DiscardedAuthenticationResponseBytes);

public static class ArdProbeServerHandshake
{
    private const int VersionBannerLength = 12;
    private const int KeyLength = 64;
    private const int AuthenticationResponseLength = 128 + KeyLength;
    private const string ModulusHex =
        "D2652EF10104A3DDC1219700EDFBD1E19F7678B4A4F6D5952634BD8BF1D60326322B5D32366DC25CB4E8E73AF4312A70D2DCAF2747EB89D7E88553EECD6A283D";

    private static readonly byte[] ServerBanner = Encoding.ASCII.GetBytes("RFB 003.889\n");
    private static readonly byte[] SecurityTypes = [1, (byte)RfbSecurityType.AppleRemoteDesktop];

    public static async Task<ArdProbeServerHandshakeResult> AcceptAsync(
        Stream stream,
        CancellationToken cancellationToken,
        Action<string>? stageObserver = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        await stream.WriteAsync(ServerBanner, cancellationToken).ConfigureAwait(false);
        stageObserver?.Invoke("Banner sent");

        var clientBanner = new byte[VersionBannerLength];
        await ReadExactlyAsync(
            stream,
            clientBanner,
            "The RFB client version banner was truncated.",
            cancellationToken).ConfigureAwait(false);
        var version = ParseClientVersion(clientBanner);
        stageObserver?.Invoke("Client version read");

        await stream.WriteAsync(SecurityTypes, cancellationToken).ConfigureAwait(false);
        var selection = new byte[1];
        await ReadExactlyAsync(
            stream,
            selection,
            "The RFB security selection was truncated.",
            cancellationToken).ConfigureAwait(false);
        stageObserver?.Invoke("Security choice read");
        if (selection[0] != (byte)RfbSecurityType.AppleRemoteDesktop)
        {
            throw new InvalidDataException("The RFB client did not select Apple Remote Desktop security.");
        }

        var challenge = CreateChallenge();
        await stream.WriteAsync(challenge, cancellationToken).ConfigureAwait(false);
        stageObserver?.Invoke("Authentication challenge sent");

        var response = new byte[AuthenticationResponseLength];
        try
        {
            await ReadExactlyAsync(
                stream,
                response,
                "The ARD authentication response was truncated.",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }

        var securityResult = new byte[sizeof(uint)];
        stageObserver?.Invoke("Authentication response read");
        BinaryPrimitives.WriteUInt32BigEndian(securityResult, 0);
        await stream.WriteAsync(securityResult, cancellationToken).ConfigureAwait(false);
        stageObserver?.Invoke("Security success sent");

        var clientInit = new byte[1];
        await ReadExactlyAsync(
            stream,
            clientInit,
            "The RFB ClientInit message was truncated.",
            cancellationToken).ConfigureAwait(false);
        stageObserver?.Invoke("ClientInit read");
        if (clientInit[0] is not 0x01 and not 0xC1)
        {
            throw new InvalidDataException("The RFB ClientInit value is not supported.");
        }

        return new ArdProbeServerHandshakeResult(version, clientInit[0], AuthenticationResponseLength);
    }

    private static RfbVersion ParseClientVersion(ReadOnlySpan<byte> banner)
    {
        if (banner.SequenceEqual(Encoding.ASCII.GetBytes(RfbVersion.V3_8.Banner)))
        {
            return RfbVersion.V3_8;
        }

        if (banner.SequenceEqual(Encoding.ASCII.GetBytes(RfbVersion.V3_889.Banner)))
        {
            return RfbVersion.V3_889;
        }

        throw new InvalidDataException("The RFB client version banner is not supported.");
    }

    private static byte[] CreateChallenge()
    {
        var modulus = Convert.FromHexString(ModulusHex);
        var prime = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var publicKey = BigInteger.ModPow(new BigInteger(5), new BigInteger(3), prime)
            .ToByteArray(isUnsigned: true, isBigEndian: true);
        try
        {
            var challenge = new byte[4 + (2 * KeyLength)];
            BinaryPrimitives.WriteUInt16BigEndian(challenge, 5);
            BinaryPrimitives.WriteUInt16BigEndian(challenge.AsSpan(sizeof(ushort)), KeyLength);
            modulus.CopyTo(challenge, 2 * sizeof(ushort));
            publicKey.CopyTo(challenge, challenge.Length - publicKey.Length);
            return challenge;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        string truncatedMessage,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var count = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new InvalidDataException(truncatedMessage);
            }

            offset += count;
        }
    }
}
