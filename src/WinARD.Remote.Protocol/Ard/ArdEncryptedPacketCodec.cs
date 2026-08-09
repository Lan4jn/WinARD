using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Errors;

namespace WinARD.Remote.Protocol.Ard;

internal static class ArdEncryptedPacketCodec
{
    internal const int MaximumPayloadLength = 65_498;
    private const int DigestLength = 20;
    private const int HeaderLength = sizeof(ushort);
    private const int SequenceLength = sizeof(uint);
    private const int BlockLength = 16;

    public static byte[] Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        uint sequence,
        ReadOnlySpan<byte> payload)
    {
        ValidateKeyAndIv(key, iv);
        if (payload.Length > MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"ARD encrypted packet payloads must not exceed {MaximumPayloadLength} bytes.");
        }

        var payloadEnd = HeaderLength + payload.Length;
        var ciphertextLength = AlignToBlock(payloadEnd + DigestLength);
        var digestOffset = ciphertextLength - DigestLength;
        var plaintext = new byte[ciphertextLength];
        var hashInput = new byte[SequenceLength + digestOffset];
        var wire = new byte[HeaderLength + ciphertextLength];
        var keyBytes = key.ToArray();
        try
        {
            BinaryPrimitives.WriteUInt16BigEndian(plaintext, checked((ushort)payload.Length));
            payload.CopyTo(plaintext.AsSpan(HeaderLength));
            BinaryPrimitives.WriteUInt32BigEndian(hashInput, sequence);
            plaintext.AsSpan(0, digestOffset).CopyTo(hashInput.AsSpan(SequenceLength));
#pragma warning disable CA5350 // SHA-1 is required by the Apple Remote Desktop encrypted packet format.
            _ = SHA1.HashData(hashInput, plaintext.AsSpan(digestOffset, DigestLength));
#pragma warning restore CA5350

            BinaryPrimitives.WriteUInt16BigEndian(wire, checked((ushort)ciphertextLength));
#pragma warning disable CA5358 // AES-CBC is required by the Apple Remote Desktop encrypted packet format.
            using var aes = Aes.Create();
            aes.Key = keyBytes;
            _ = aes.EncryptCbc(
                plaintext,
                iv,
                wire.AsSpan(HeaderLength),
                PaddingMode.None);
#pragma warning restore CA5358
            return wire;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(wire);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(hashInput);
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    public static ArdDecryptedPacket Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        uint sequence,
        ReadOnlySpan<byte> ciphertext)
    {
        ValidateKeyAndIv(key, iv);
        if (ciphertext.IsEmpty || ciphertext.Length % BlockLength != 0 || ciphertext.Length > ushort.MaxValue)
        {
            throw PacketFailure(
                "ARD encrypted packet ciphertext length is invalid.",
                RfbProtocolFailureKind.ArdEncryptionPacket,
                ArdEncryptedPacketFailureStage.OuterLength,
                sequence,
                ciphertext.Length);
        }

        var plaintext = new byte[ciphertext.Length];
        byte[]? hashInput = null;
        var keyBytes = key.ToArray();
        byte[]? payload = null;
        byte[]? nextIv = null;
        try
        {
#pragma warning disable CA5358 // AES-CBC is required by the Apple Remote Desktop encrypted packet format.
            using var aes = Aes.Create();
            aes.Key = keyBytes;
            _ = aes.DecryptCbc(ciphertext, iv, plaintext, PaddingMode.None);
#pragma warning restore CA5358
            if (plaintext.Length < HeaderLength + DigestLength)
            {
                throw PacketFailure(
                    "ARD encrypted packet plaintext is too short.",
                    RfbProtocolFailureKind.ArdEncryptionPacket,
                    ArdEncryptedPacketFailureStage.PlaintextTooShort,
                    sequence,
                    ciphertext.Length);
            }

            var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(plaintext);
            var digestOffset = plaintext.Length - DigestLength;
            var payloadEnd = HeaderLength + payloadLength;
            if (payloadEnd > digestOffset)
            {
                throw PacketFailure(
                    "ARD encrypted packet payload length exceeds the decrypted packet.",
                    RfbProtocolFailureKind.ArdEncryptionPacket,
                    ArdEncryptedPacketFailureStage.PayloadLength,
                    sequence,
                    ciphertext.Length);
            }

            hashInput = new byte[SequenceLength + digestOffset];
            BinaryPrimitives.WriteUInt32BigEndian(hashInput, sequence);
            plaintext.AsSpan(0, digestOffset).CopyTo(hashInput.AsSpan(SequenceLength));
            Span<byte> expectedDigest = stackalloc byte[DigestLength];
#pragma warning disable CA5350 // SHA-1 is required by the Apple Remote Desktop encrypted packet format.
            _ = SHA1.HashData(hashInput, expectedDigest);
#pragma warning restore CA5350
            if (!CryptographicOperations.FixedTimeEquals(
                    expectedDigest,
                    plaintext.AsSpan(digestOffset, DigestLength)))
            {
                throw PacketFailure(
                    "ARD encrypted packet integrity validation failed.",
                    RfbProtocolFailureKind.ArdEncryptionIntegrity,
                    ArdEncryptedPacketFailureStage.Integrity,
                    sequence,
                    ciphertext.Length);
            }

            payload = plaintext.AsSpan(HeaderLength, payloadLength).ToArray();
            nextIv = ciphertext[^BlockLength..].ToArray();
            var result = new ArdDecryptedPacket(payload, nextIv);
            payload = null;
            nextIv = null;
            return result;
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(DecryptFailureInfo(sequence, ciphertext.Length));
        }
        catch (CryptographicException exception)
        {
            throw CreateCbcDecryptFailure(exception, sequence, ciphertext.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (hashInput is not null)
            {
                CryptographicOperations.ZeroMemory(hashInput);
            }

            CryptographicOperations.ZeroMemory(keyBytes);
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            if (nextIv is not null)
            {
                CryptographicOperations.ZeroMemory(nextIv);
            }
        }
    }

    internal static RfbProtocolException CreateCbcDecryptFailure(
        CryptographicException exception,
        uint sequence,
        int ciphertextLength) =>
        new(
            "ARD encrypted packet decryption failed.",
            exception,
            DecryptFailureInfo(
                sequence,
                ciphertextLength,
                RfbProtocolFailureKind.ArdEncryptionPacket,
                ArdEncryptedPacketFailureStage.CbcDecrypt));

    private static int AlignToBlock(int length) => checked(((length + BlockLength - 1) / BlockLength) * BlockLength);

    private static void ValidateKeyAndIv(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != BlockLength)
        {
            throw new ArgumentException("The ARD encryption key must be 16 bytes.", nameof(key));
        }

        if (iv.Length != BlockLength)
        {
            throw new ArgumentException("The ARD encryption IV must be 16 bytes.", nameof(iv));
        }
    }

    private static RfbProtocolException PacketFailure(
        string message,
        RfbProtocolFailureKind kind,
        ArdEncryptedPacketFailureStage stage,
        uint sequence,
        int ciphertextLength) =>
        RfbProtocolException.Create(
            message,
            DecryptFailureInfo(sequence, ciphertextLength, kind, stage));

    private static RfbProtocolFailureInfo DecryptFailureInfo(
        uint sequence,
        int ciphertextLength,
        RfbProtocolFailureKind kind = RfbProtocolFailureKind.ArdEncryptionPacket,
        ArdEncryptedPacketFailureStage? stage = null) =>
        new(
            kind,
            ArdEncryptionStage: stage,
            ArdEncryptionDirection: ArdEncryptedPacketDirection.Receive,
            ArdEncryptionSequence: sequence,
            ArdCiphertextLength: ciphertextLength);
}

internal sealed class ArdDecryptedPacket : IDisposable
{
    private byte[]? _payload;
    private byte[]? _nextIv;

    public ArdDecryptedPacket(byte[] payload, byte[] nextIv)
    {
        _payload = payload;
        _nextIv = nextIv;
    }

    public byte[] Payload => _payload ?? throw new ObjectDisposedException(nameof(ArdDecryptedPacket));

    public byte[] NextIv => _nextIv ?? throw new ObjectDisposedException(nameof(ArdDecryptedPacket));

    public void Dispose()
    {
        var payload = Interlocked.Exchange(ref _payload, null);
        var nextIv = Interlocked.Exchange(ref _nextIv, null);
        if (payload is not null)
        {
            CryptographicOperations.ZeroMemory(payload);
        }

        if (nextIv is not null)
        {
            CryptographicOperations.ZeroMemory(nextIv);
        }
    }
}
