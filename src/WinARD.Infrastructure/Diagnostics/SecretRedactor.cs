using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace WinARD.Infrastructure.Diagnostics;

/// <summary>
/// Redacts byte-backed secret variants from diagnostic text. Registered secrets and variants are
/// retained only in zeroable byte arrays; they are never placed in a string collection.
/// </summary>
public sealed class SecretRedactor : IDisposable
{
    public const string RedactedValue = "[REDACTED]";
    public const string Version = "1";
    private const int MinimumSecretBytes = 4;
    private static readonly byte[] Replacement = Encoding.UTF8.GetBytes(RedactedValue);
    private readonly object _sync = new();
    private readonly List<RegistrationEntry> _entries = [];
    private bool _disposed;

    public int RegisteredVariantCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Sum(entry => entry.Variants.Count);
            }
        }
    }

    public IDisposable Register(ReadOnlySpan<char> secret)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(secret)];
        Encoding.UTF8.GetBytes(secret, bytes);
        try
        {
            return Register(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public IDisposable Register(ReadOnlySpan<byte> secret)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (secret.Length < MinimumSecretBytes)
            {
                return EmptyRegistration.Instance;
            }

            var entry = new RegistrationEntry(BuildVariants(secret));
            _entries.Add(entry);
            return new Registration(this, entry);
        }
    }

    public string Redact(string? text)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.IsNullOrEmpty(text) || _entries.Count == 0)
            {
                return text ?? string.Empty;
            }

            var source = Encoding.UTF8.GetBytes(text);
            try
            {
                var patterns = _entries
                    .SelectMany(entry => entry.Variants)
                    .OrderByDescending(static variant => variant.Length)
                    .ToArray();
                var masked = new bool[source.Length];
                for (var offset = 0; offset < source.Length; offset++)
                {
                    foreach (var pattern in patterns)
                    {
                        if (pattern.Length == 0 || offset + pattern.Length > source.Length ||
                            masked.AsSpan(offset, pattern.Length).Contains(true) ||
                            !source.AsSpan(offset, pattern.Length).SequenceEqual(pattern))
                        {
                            continue;
                        }

                        masked.AsSpan(offset, pattern.Length).Fill(true);
                        break;
                    }
                }

                if (!masked.Contains(true))
                {
                    return text;
                }

                using var output = new MemoryStream(source.Length);
                for (var offset = 0; offset < source.Length;)
                {
                    if (!masked[offset])
                    {
                        output.WriteByte(source[offset++]);
                        continue;
                    }

                    output.Write(Replacement);
                    while (offset < source.Length && masked[offset])
                    {
                        offset++;
                    }
                }

                return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(source);
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ClearCore();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            ClearCore();
            _disposed = true;
        }
    }

    private void Remove(RegistrationEntry entry)
    {
        lock (_sync)
        {
            if (_entries.Remove(entry))
            {
                entry.Zero();
            }
        }
    }

    private void ClearCore()
    {
        foreach (var entry in _entries)
        {
            entry.Zero();
        }

        _entries.Clear();
    }

    private static List<byte[]> BuildVariants(ReadOnlySpan<byte> secret)
    {
        List<byte[]> variants = [];
        AddDistinct(variants, secret.ToArray());
        AddDistinct(variants, EncodeBase64(secret));
        var url = UrlEncode(secret, lowerHex: false);
        AddDistinct(variants, url);
        AddDistinct(variants, UrlEncode(secret, lowerHex: true));
        AddDistinct(variants, JsonEscape(secret));
        return variants;
    }

    private static byte[] EncodeBase64(ReadOnlySpan<byte> secret)
    {
        var buffer = new byte[System.Buffers.Text.Base64.GetMaxEncodedToUtf8Length(secret.Length)];
        var status = System.Buffers.Text.Base64.EncodeToUtf8(secret, buffer, out _, out var written);
        if (status != System.Buffers.OperationStatus.Done)
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw new InvalidOperationException("Secret base64 variant could not be encoded.");
        }

        if (written == buffer.Length)
        {
            return buffer;
        }

        var result = buffer.AsSpan(0, written).ToArray();
        CryptographicOperations.ZeroMemory(buffer);
        return result;
    }

    private static byte[] UrlEncode(ReadOnlySpan<byte> secret, bool lowerHex)
    {
        const string upper = "0123456789ABCDEF";
        const string lower = "0123456789abcdef";
        var hex = lowerHex ? lower : upper;
        using var output = new MemoryStream(secret.Length * 3);
        foreach (var value in secret)
        {
            if ((value >= (byte)'a' && value <= (byte)'z') ||
                (value >= (byte)'A' && value <= (byte)'Z') ||
                (value >= (byte)'0' && value <= (byte)'9') ||
                value is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                output.WriteByte(value);
            }
            else
            {
                output.WriteByte((byte)'%');
                output.WriteByte((byte)hex[value >> 4]);
                output.WriteByte((byte)hex[value & 0x0f]);
            }
        }

        return output.ToArray();
    }

    private static byte[] JsonEscape(ReadOnlySpan<byte> secret)
    {
        using var output = new MemoryStream(secret.Length * 2);
        foreach (var value in secret)
        {
            switch (value)
            {
                case (byte)'"':
                    output.Write("\\\""u8);
                    break;
                case (byte)'\\':
                    output.Write("\\\\"u8);
                    break;
                case (byte)'\b':
                    output.Write("\\b"u8);
                    break;
                case (byte)'\f':
                    output.Write("\\f"u8);
                    break;
                case (byte)'\n':
                    output.Write("\\n"u8);
                    break;
                case (byte)'\r':
                    output.Write("\\r"u8);
                    break;
                case (byte)'\t':
                    output.Write("\\t"u8);
                    break;
                default:
                    output.WriteByte(value);
                    break;
            }
        }

        return output.ToArray();
    }

    private static void AddDistinct(List<byte[]> variants, byte[] candidate)
    {
        if (candidate.Length < MinimumSecretBytes ||
            variants.Any(existing => existing.AsSpan().SequenceEqual(candidate)))
        {
            CryptographicOperations.ZeroMemory(candidate);
            return;
        }

        variants.Add(candidate);
    }

    private sealed class Registration(SecretRedactor owner, RegistrationEntry entry) : IDisposable
    {
        private SecretRedactor? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(entry);
    }

    private sealed class EmptyRegistration : IDisposable
    {
        public static EmptyRegistration Instance { get; } = new();

        public void Dispose() { }
    }

    private sealed class RegistrationEntry(List<byte[]> variants)
    {
        public List<byte[]> Variants { get; } = variants;

        public void Zero()
        {
            foreach (var variant in Variants)
            {
                CryptographicOperations.ZeroMemory(variant);
            }

            Variants.Clear();
        }
    }
}
