using System.Buffers.Binary;
using System.Security.Cryptography;
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
    private const uint Sequence = 0x10203040;

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
        var packet = ArdEncryptedPacketCodec.Encrypt(Key, InitialIv, Sequence, PointerPayload);
        packet[^1] ^= 0x01;

        var exception = Assert.Throws<RfbProtocolException>(() =>
            ArdEncryptedPacketCodec.Decrypt(Key, InitialIv, Sequence, packet.AsSpan(2)));

        AssertFailure(
            exception,
            RfbProtocolFailureKind.ArdEncryptionIntegrity,
            ArdEncryptedPacketFailureStage.Integrity,
            packet.Length - sizeof(ushort));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(65536)]
    public void Decrypt_rejects_invalid_ciphertext_length(int length)
    {
        var exception = Assert.Throws<RfbProtocolException>(() =>
            ArdEncryptedPacketCodec.Decrypt(Key, InitialIv, Sequence, new byte[length]));

        AssertFailure(
            exception,
            RfbProtocolFailureKind.ArdEncryptionPacket,
            ArdEncryptedPacketFailureStage.OuterLength,
            length);
    }

    [Fact]
    public void Decrypt_rejects_plaintext_that_is_too_short()
    {
        var ciphertext = EncryptFixturePlaintext(new byte[16]);

        var exception = Assert.Throws<RfbProtocolException>(() =>
            ArdEncryptedPacketCodec.Decrypt(Key, InitialIv, Sequence, ciphertext));

        AssertFailure(
            exception,
            RfbProtocolFailureKind.ArdEncryptionPacket,
            ArdEncryptedPacketFailureStage.PlaintextTooShort,
            ciphertext.Length);
    }

    [Fact]
    public void Decrypt_rejects_payload_length_beyond_digest()
    {
        var plaintext = new byte[32];
        BinaryPrimitives.WriteUInt16BigEndian(plaintext, 11);
        var ciphertext = EncryptFixturePlaintext(plaintext);

        var exception = Assert.Throws<RfbProtocolException>(() =>
            ArdEncryptedPacketCodec.Decrypt(Key, InitialIv, Sequence, ciphertext));

        AssertFailure(
            exception,
            RfbProtocolFailureKind.ArdEncryptionPacket,
            ArdEncryptedPacketFailureStage.PayloadLength,
            ciphertext.Length);
    }

    [Fact]
    public void Decrypt_rejects_non_zero_padding()
    {
        var plaintext = new byte[32];
        plaintext[2] = 0x01;
        var ciphertext = EncryptFixturePlaintext(plaintext);

        var exception = Assert.Throws<RfbProtocolException>(() =>
            ArdEncryptedPacketCodec.Decrypt(Key, InitialIv, Sequence, ciphertext));

        AssertFailure(
            exception,
            RfbProtocolFailureKind.ArdEncryptionPacket,
            ArdEncryptedPacketFailureStage.Padding,
            ciphertext.Length);
    }

    [Fact]
    public void CreateCbcDecryptFailure_preserves_inner_exception_and_safe_receive_context()
    {
        const string secretMarker = "cbc-secret-marker-8A2B7F";
        var innerException = new CryptographicException(secretMarker);

        var exception = ArdEncryptedPacketCodec.CreateCbcDecryptFailure(innerException, 7, 48);

        Assert.Same(innerException, exception.InnerException);
        Assert.Equal(RfbProtocolFailureKind.ArdEncryptionPacket, exception.Failure?.Kind);
        Assert.Equal(ArdEncryptedPacketFailureStage.CbcDecrypt, exception.Failure?.ArdEncryptionStage);
        Assert.Equal(ArdEncryptedPacketDirection.Receive, exception.Failure?.ArdEncryptionDirection);
        Assert.Equal(7U, exception.Failure?.ArdEncryptionSequence);
        Assert.Equal(48, exception.Failure?.ArdCiphertextLength);
        Assert.DoesNotContain(secretMarker, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretMarker, exception.Failure?.ToString() ?? string.Empty, StringComparison.Ordinal);
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

    private static byte[] EncryptFixturePlaintext(ReadOnlySpan<byte> plaintext)
    {
#pragma warning disable CA5358 // AES-CBC is required by the Apple Remote Desktop encrypted packet format.
        using var aes = Aes.Create();
        aes.Key = Key;
        return aes.EncryptCbc(plaintext, InitialIv, PaddingMode.None);
#pragma warning restore CA5358
    }

    private static void AssertFailure(
        RfbProtocolException exception,
        RfbProtocolFailureKind kind,
        ArdEncryptedPacketFailureStage stage,
        int ciphertextLength)
    {
        Assert.Equal(kind, exception.Failure?.Kind);
        Assert.Equal(stage, exception.Failure?.ArdEncryptionStage);
        Assert.Equal(ArdEncryptedPacketDirection.Receive, exception.Failure?.ArdEncryptionDirection);
        Assert.Equal(Sequence, exception.Failure?.ArdEncryptionSequence);
        Assert.Equal(ciphertextLength, exception.Failure?.ArdCiphertextLength);
    }
}
