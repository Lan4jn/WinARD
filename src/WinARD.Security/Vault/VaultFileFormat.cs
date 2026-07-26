using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WinARD.Security.Vault;

public sealed record VaultKdfParameters(
    int MemoryKiB,
    int Iterations,
    int Parallelism)
{
    public static VaultKdfParameters Default { get; } = new(65_536, 3, 2);
}

internal sealed record VaultEntry(
    string Reference,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag);

internal sealed record VaultDocument(
    VaultKdfParameters Kdf,
    byte[] Salt,
    byte[] VerifierTag,
    long Revision,
    IReadOnlyDictionary<string, VaultEntry> Entries,
    byte[] ManifestNonce,
    byte[] ManifestTag,
    byte[]? ManifestData = null);

internal static class VaultFileFormat
{
    private static readonly byte[] Magic = "WARDVLT1"u8.ToArray();

    public const int Version = 2;
    public const int SaltSize = 16;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    public const int MaximumEntries = 10_000;
    public const int MaximumReferenceBytes = 1024;
    public const int MaximumSecretBytes = 1024 * 1024;
    public const int MaximumMemoryKiB = 256 * 1024;
    public const int MaximumIterations = 10;
    public const int MaximumParallelism = 16;

    public static byte[] Serialize(VaultDocument document)
    {
        var ownsManifest = document.ManifestData is null;
        var manifest = document.ManifestData ?? BuildManifest(document);
        try
        {
            if (document.ManifestTag.Length != TagSize)
            {
                throw new InvalidDataException("The vault manifest tag length is invalid.");
            }

            if (document.ManifestNonce.Length != NonceSize)
            {
                throw new InvalidDataException("The vault manifest nonce length is invalid.");
            }

            var result = new byte[
                manifest.Length + document.ManifestNonce.Length + document.ManifestTag.Length];
            manifest.CopyTo(result, 0);
            document.ManifestNonce.CopyTo(result, manifest.Length);
            document.ManifestTag.CopyTo(
                result,
                manifest.Length + document.ManifestNonce.Length);
            if (result.Length > MaximumFileBytes)
            {
                throw new InvalidDataException("The vault file exceeds its size limit.");
            }

            return result;
        }
        finally
        {
            if (ownsManifest)
            {
                CryptographicOperations.ZeroMemory(manifest);
            }
        }
    }

    public static byte[] BuildManifest(VaultDocument document)
    {
        ValidateKdf(document.Kdf);
        if (document.Salt.Length != SaltSize)
        {
            throw new InvalidDataException("The vault salt length is invalid.");
        }

        if (document.VerifierTag.Length != TagSize)
        {
            throw new InvalidDataException("The vault verifier length is invalid.");
        }

        if (document.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException("The vault has too many entries.");
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(document.Kdf.MemoryKiB);
        writer.Write(document.Kdf.Iterations);
        writer.Write(document.Kdf.Parallelism);
        writer.Write(document.Salt);
        writer.Write(document.VerifierTag);
        writer.Write(document.Revision);
        writer.Write(document.Entries.Count);
        foreach (var pair in document.Entries.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var entry = pair.Value;
            var reference = Encoding.UTF8.GetBytes(entry.Reference);
            try
            {
                ValidateEntry(entry, reference.Length);
                writer.Write(reference.Length);
                writer.Write(reference);
                writer.Write(entry.Nonce);
                writer.Write(entry.Ciphertext.Length);
                writer.Write(entry.Ciphertext);
                writer.Write(entry.Tag);
            }
            finally
            {
                Array.Clear(reference);
            }
        }

        writer.Flush();
        if (stream.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The vault file exceeds its size limit.");
        }

        return stream.ToArray();
    }

    public static VaultDocument Parse(byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (contents.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The vault file exceeds its size limit.");
        }

        try
        {
            using var stream = new MemoryStream(contents, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            {
                throw new InvalidDataException("The vault magic is invalid.");
            }

            if (reader.ReadInt32() != Version)
            {
                throw new InvalidDataException("The vault version is unsupported.");
            }

            var kdf = new VaultKdfParameters(
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32());
            ValidateKdf(kdf);
            var salt = ReadExact(reader, SaltSize);
            var verifierTag = ReadExact(reader, TagSize);
            var revision = reader.ReadInt64();
            if (revision < 0)
            {
                throw new InvalidDataException("The vault revision is invalid.");
            }

            var count = reader.ReadInt32();
            if (count is < 0 or > MaximumEntries)
            {
                throw new InvalidDataException("The vault entry count is invalid.");
            }

            var entries = new Dictionary<string, VaultEntry>(count, StringComparer.Ordinal);
            var nonces = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                var referenceLength = reader.ReadInt32();
                if (referenceLength is < 1 or > MaximumReferenceBytes)
                {
                    throw new InvalidDataException("A vault reference length is invalid.");
                }

                var referenceBytes = ReadExact(reader, referenceLength);
                string reference;
                try
                {
                    reference = new UTF8Encoding(false, true).GetString(referenceBytes);
                }
                finally
                {
                    Array.Clear(referenceBytes);
                }

                var nonce = ReadExact(reader, NonceSize);
                if (nonce.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                {
                    throw new InvalidDataException(
                        "A vault entry uses the reserved verifier nonce.");
                }

                var ciphertextLength = reader.ReadInt32();
                if (ciphertextLength is < 0 or > MaximumSecretBytes)
                {
                    throw new InvalidDataException("A vault ciphertext length is invalid.");
                }

                var ciphertext = ReadExact(reader, ciphertextLength);
                var tag = ReadExact(reader, TagSize);
                var entry = new VaultEntry(reference, nonce, ciphertext, tag);
                if (!entries.TryAdd(reference, entry))
                {
                    throw new InvalidDataException("The vault contains a duplicate reference.");
                }

                if (!nonces.Add(Convert.ToHexString(nonce)))
                {
                    throw new InvalidDataException("The vault contains a duplicate nonce.");
                }
            }

            var manifestLength = checked((int)stream.Position);
            var manifestNonce = ReadExact(reader, NonceSize);
            if (manifestNonce.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                throw new InvalidDataException(
                    "The vault manifest nonce collides with the verifier nonce.");
            }

            if (entries.Values.Any(
                entry => entry.Nonce.AsSpan().SequenceEqual(manifestNonce)))
            {
                throw new InvalidDataException(
                    "The vault manifest nonce duplicates an entry nonce.");
            }

            var manifestTag = ReadExact(reader, TagSize);
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("The vault contains trailing data.");
            }

            return new VaultDocument(
                kdf,
                salt,
                verifierTag,
                revision,
                entries,
                manifestNonce,
                manifestTag,
                contents.AsSpan(0, manifestLength).ToArray());
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The vault file is truncated.", exception);
        }
    }

    public static byte[] AssociatedData(
        VaultKdfParameters kdf,
        ReadOnlySpan<byte> salt,
        string reference,
        ReadOnlySpan<byte> nonce,
        int ciphertextLength)
    {
        var referenceBytes = Encoding.UTF8.GetBytes(reference);
        try
        {
            var result = new byte[
                Magic.Length + (6 * sizeof(int)) + SaltSize +
                sizeof(int) + referenceBytes.Length + NonceSize];
            var offset = 0;
            Magic.CopyTo(result, offset);
            offset += Magic.Length;
            WriteInt(result, ref offset, Version);
            WriteInt(result, ref offset, kdf.MemoryKiB);
            WriteInt(result, ref offset, kdf.Iterations);
            WriteInt(result, ref offset, kdf.Parallelism);
            salt.CopyTo(result.AsSpan(offset));
            offset += SaltSize;
            WriteInt(result, ref offset, referenceBytes.Length);
            referenceBytes.CopyTo(result, offset);
            offset += referenceBytes.Length;
            nonce.CopyTo(result.AsSpan(offset));
            offset += NonceSize;
            WriteInt(result, ref offset, ciphertextLength);
            return result;
        }
        finally
        {
            Array.Clear(referenceBytes);
        }
    }

    public static byte[] HeaderAssociatedData(
        VaultKdfParameters kdf,
        ReadOnlySpan<byte> salt)
    {
        var result = new byte[Magic.Length + (4 * sizeof(int)) + SaltSize];
        var offset = 0;
        Magic.CopyTo(result, offset);
        offset += Magic.Length;
        WriteInt(result, ref offset, Version);
        WriteInt(result, ref offset, kdf.MemoryKiB);
        WriteInt(result, ref offset, kdf.Iterations);
        WriteInt(result, ref offset, kdf.Parallelism);
        salt.CopyTo(result.AsSpan(offset));
        return result;
    }

    private static void ValidateEntry(VaultEntry entry, int referenceLength)
    {
        if (referenceLength is < 1 or > MaximumReferenceBytes ||
            entry.Nonce.Length != NonceSize ||
            entry.Tag.Length != TagSize ||
            entry.Ciphertext.Length > MaximumSecretBytes)
        {
            throw new InvalidDataException("A vault entry is invalid.");
        }
    }

    private static void ValidateKdf(VaultKdfParameters kdf)
    {
        if (kdf.MemoryKiB is < 8192 or > MaximumMemoryKiB ||
            kdf.Iterations is < 1 or > MaximumIterations ||
            kdf.Parallelism is < 1 or > MaximumParallelism)
        {
            throw new InvalidDataException("The vault KDF parameters are outside safe limits.");
        }
    }

    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        var value = reader.ReadBytes(length);
        if (value.Length != length)
        {
            throw new EndOfStreamException();
        }

        return value;
    }

    private static void WriteInt(byte[] target, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset), value);
        offset += sizeof(int);
    }
}
