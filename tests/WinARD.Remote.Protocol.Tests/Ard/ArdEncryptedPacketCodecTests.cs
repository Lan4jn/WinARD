using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Errors;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Ard;

public sealed class ArdEncryptedPacketCodecTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
    private static readonly byte[] InitialIv = Enumerable.Range(16, 16).Select(value => (byte)value).ToArray();
    private static readonly byte[] PointerPayload = [5, 0, 0, 100, 0, 200];

    [Fact]
    public void Encrypt_matches_independent_known_answer_vector()
    {
        var packet = ArdEncryptedPacketCodec.Encrypt(Key, InitialIv, 0, PointerPayload);

        Assert.Equal(
            Convert.FromHexString(
                "0020FE2E2CACB8418991A8CDB1A23857886887970E159D647B0D7435DDE53A20E920"),
            packet);
    }

    [Fact]
    public void Decrypt_returns_payload_and_last_ciphertext_block_as_next_iv()
    {
        var packet = ArdEncryptedPacketCodec.Encrypt(Key, InitialIv, 0, PointerPayload);

        using var decoded = ArdEncryptedPacketCodec.Decrypt(Key, InitialIv, 0, packet.AsSpan(2));

        Assert.Equal(PointerPayload, decoded.Payload);
        Assert.Equal(packet[^16..], decoded.NextIv);
    }

    [Fact]
    public void Decrypt_rejects_sha1_mismatch()
    {
        var packet = ArdEncryptedPacketCodec.Encrypt(Key, InitialIv, 0, PointerPayload);
        packet[^1] ^= 0x01;

        var exception = Assert.Throws<RfbProtocolException>(() =>
            ArdEncryptedPacketCodec.Decrypt(Key, InitialIv, 0, packet.AsSpan(2)));

        Assert.Equal(RfbProtocolFailureKind.ArdEncryptionIntegrity, exception.Failure?.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public void Decrypt_rejects_empty_or_unaligned_ciphertext(int length)
    {
        var exception = Assert.Throws<RfbProtocolException>(() =>
            ArdEncryptedPacketCodec.Decrypt(Key, InitialIv, 0, new byte[length]));

        Assert.Equal(RfbProtocolFailureKind.ArdEncryptionPacket, exception.Failure?.Kind);
    }

    [Fact]
    public void Cipher_material_takes_ownership_and_clears_key_and_iv()
    {
        var key = Key.ToArray();
        var iv = InitialIv.ToArray();
        var material = new ArdSessionCipherMaterial(key, iv);

        material.Dispose();

        Assert.All(key, value => Assert.Equal(0, value));
        Assert.All(iv, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Encrypt_rejects_payload_that_cannot_fit_the_ushort_ciphertext_length()
    {
        var payload = new byte[ArdEncryptedPacketCodec.MaximumPayloadLength + 1];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ArdEncryptedPacketCodec.Encrypt(Key, InitialIv, 0, payload));
    }
}
