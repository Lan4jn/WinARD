using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace WinARD.Infrastructure.Diagnostics;

public sealed record SecretRedactorLimits(
    int MaxSecretBytes = 1_024,
    int MaxRegisteredSecrets = 16,
    int MaxVariantsPerSecret = 7,
    int MaxTextUtf8Bytes = 64 * 1_024);

/// <summary>
/// Redacts byte-backed secret variants from diagnostic text. Registered secrets and variants are
/// retained only in zeroable byte arrays; they are never placed in a string collection. Default
/// work is bounded to 16 secrets * 7 variants * 64 KiB of UTF-8 text per redaction call. Oversized
/// text fails closed to <see cref="RedactedValue"/>; when a configured text limit is smaller than
/// that marker, the marker itself is truncated at a valid UTF-8 rune boundary and no source prefix
/// is returned.
/// </summary>
public sealed class SecretRedactor : IDisposable
{
    public const string RedactedValue = "[REDACTED]";
    public const string Version = "1";
    private const int MinimumSecretBytes = 4;
    private static readonly byte[] Replacement = Encoding.UTF8.GetBytes(RedactedValue);
    private readonly object _sync = new();
    private readonly List<RegistrationEntry> _entries = [];
    private readonly SecretRedactorLimits _limits;
    private bool _disposed;

    public SecretRedactor()
        : this(new SecretRedactorLimits())
    {
    }

    public SecretRedactor(SecretRedactorLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxSecretBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxRegisteredSecrets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxVariantsPerSecret);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_limits.MaxVariantsPerSecret, 7);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxTextUtf8Bytes);
    }

    public int RegisteredSecretCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

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
        if (secret.Length > _limits.MaxSecretBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), "Secret exceeds the configured byte limit.");
        }

        var byteCount = Encoding.UTF8.GetByteCount(secret);
        if (byteCount > _limits.MaxSecretBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), "Secret exceeds the configured byte limit.");
        }

        var bytes = new byte[byteCount];
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
        if (secret.Length > _limits.MaxSecretBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), "Secret exceeds the configured byte limit.");
        }

        if (secret.Length < MinimumSecretBytes)
        {
            return EmptyRegistration.Instance;
        }

        var variants = BuildVariants(secret, _limits.MaxVariantsPerSecret);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entries.Count >= _limits.MaxRegisteredSecrets)
                {
                    throw new InvalidOperationException("The secret registration limit has been reached.");
                }

                var entry = new RegistrationEntry(variants);
                _entries.Add(entry);
                variants = null!;
                return new Registration(this, entry);
            }
        }
        finally
        {
            if (variants is not null)
            {
                ZeroVariants(variants);
            }
        }
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            return text ?? string.Empty;
        }

        if (ExceedsUtf8Limit(text, _limits.MaxTextUtf8Bytes))
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            return LimitUtf8(RedactedValue, _limits.MaxTextUtf8Bytes);
        }

        List<byte[]> snapshot = [];
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Count == 0)
            {
                return text;
            }

            try
            {
                foreach (var entry in _entries)
                {
                    foreach (var variant in entry.Variants)
                    {
                        var copy = variant.ToArray();
                        try
                        {
                            snapshot.Add(copy);
                        }
                        catch
                        {
                            CryptographicOperations.ZeroMemory(copy);
                            throw;
                        }
                    }
                }
            }
            catch
            {
                ZeroVariants(snapshot);
                throw;
            }
        }

        byte[]? source = null;
        try
        {
            source = Encoding.UTF8.GetBytes(text);
            snapshot.Sort(static (left, right) => right.Length.CompareTo(left.Length));
            var masked = new bool[source.Length];
            foreach (var pattern in snapshot)
            {
                var searchOffset = 0;
                while (searchOffset <= source.Length - pattern.Length)
                {
                    var relative = source.AsSpan(searchOffset).IndexOf(pattern);
                    if (relative < 0)
                    {
                        break;
                    }

                    var matchOffset = searchOffset + relative;
                    if (!masked.AsSpan(matchOffset, pattern.Length).Contains(true))
                    {
                        masked.AsSpan(matchOffset, pattern.Length).Fill(true);
                    }

                    searchOffset = matchOffset + Math.Max(1, pattern.Length);
                }
            }

            if (!masked.Contains(true))
            {
                return text;
            }

            using var output = new MemoryStream(source.Length);
            try
            {
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

                return Encoding.UTF8.GetString(
                    output.GetBuffer(),
                    0,
                    checked((int)output.Length));
            }
            finally
            {
                ZeroBuffer(output);
            }
        }
        finally
        {
            if (source is not null)
            {
                CryptographicOperations.ZeroMemory(source);
            }

            ZeroVariants(snapshot);
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

    private static List<byte[]> BuildVariants(ReadOnlySpan<byte> secret, int maxVariants)
    {
        List<byte[]> variants = [];
        try
        {
            AddDistinct(variants, secret.ToArray(), maxVariants);
            AddDistinct(variants, EncodeBase64(secret), maxVariants);
            AddDistinct(variants, UrlEncode(secret, lowerHex: false), maxVariants);
            AddDistinct(variants, UrlEncode(secret, lowerHex: true), maxVariants);
            AddDistinct(variants, JsonEscape(secret), maxVariants);
            AddDistinct(variants, JsonUnicodeEscape(secret, lowerHex: false), maxVariants);
            AddDistinct(variants, JsonUnicodeEscape(secret, lowerHex: true), maxVariants);
            return variants;
        }
        catch
        {
            ZeroVariants(variants);
            throw;
        }
    }

    private static byte[] EncodeBase64(ReadOnlySpan<byte> secret)
    {
        byte[]? buffer = new byte[System.Buffers.Text.Base64.GetMaxEncodedToUtf8Length(secret.Length)];
        try
        {
            var status = System.Buffers.Text.Base64.EncodeToUtf8(secret, buffer, out _, out var written);
            if (status != System.Buffers.OperationStatus.Done)
            {
                throw new InvalidOperationException("Secret base64 variant could not be encoded.");
            }

            if (written == buffer.Length)
            {
                var result = buffer;
                buffer = null;
                return result;
            }

            return buffer.AsSpan(0, written).ToArray();
        }
        finally
        {
            if (buffer is not null)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private static byte[] UrlEncode(ReadOnlySpan<byte> secret, bool lowerHex)
    {
        const string upper = "0123456789ABCDEF";
        const string lower = "0123456789abcdef";
        var hex = lowerHex ? lower : upper;
        using var output = new MemoryStream(secret.Length * 3);
        try
        {
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
        finally
        {
            ZeroBuffer(output);
        }
    }

    private static byte[] JsonEscape(ReadOnlySpan<byte> secret)
    {
        using var output = new MemoryStream(secret.Length * 2);
        try
        {
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
        finally
        {
            ZeroBuffer(output);
        }
    }

    private static byte[] JsonUnicodeEscape(ReadOnlySpan<byte> secret, bool lowerHex)
    {
        const string upper = "0123456789ABCDEF";
        const string lower = "0123456789abcdef";
        var hex = lowerHex ? lower : upper;
        using var output = new MemoryStream(secret.Length * 6);
        try
        {
            while (!secret.IsEmpty)
            {
                var status = Rune.DecodeFromUtf8(secret, out var rune, out var consumed);
                if (status != OperationStatus.Done)
                {
                    return [];
                }

                secret = secret[consumed..];
                if (rune.IsAscii)
                {
                    WriteJsonAscii(output, (byte)rune.Value, hex);
                    continue;
                }

                if (rune.Value <= char.MaxValue)
                {
                    WriteUnicodeEscape(output, (ushort)rune.Value, hex);
                    continue;
                }

                var scalar = rune.Value - 0x10000;
                WriteUnicodeEscape(output, (ushort)(0xD800 + (scalar >> 10)), hex);
                WriteUnicodeEscape(output, (ushort)(0xDC00 + (scalar & 0x3FF)), hex);
            }

            return output.ToArray();
        }
        finally
        {
            ZeroBuffer(output);
        }
    }

    private static void WriteJsonAscii(Stream output, byte value, string hex)
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
            case < 0x20:
            case (byte)'&':
            case (byte)'\'':
            case (byte)'<':
            case (byte)'>':
                WriteUnicodeEscape(output, value, hex);
                break;
            default:
                output.WriteByte(value);
                break;
        }
    }

    private static void WriteUnicodeEscape(Stream output, ushort value, string hex)
    {
        output.WriteByte((byte)'\\');
        output.WriteByte((byte)'u');
        output.WriteByte((byte)hex[(value >> 12) & 0xF]);
        output.WriteByte((byte)hex[(value >> 8) & 0xF]);
        output.WriteByte((byte)hex[(value >> 4) & 0xF]);
        output.WriteByte((byte)hex[value & 0xF]);
    }

    private static void ZeroBuffer(MemoryStream stream)
    {
        if (stream.TryGetBuffer(out var buffer))
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, checked((int)stream.Length)));
        }
    }

    private static string LimitUtf8(string value, int maxBytes)
    {
        var consumedChars = 0;
        var consumedBytes = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var charsConsumed);
            if (status != OperationStatus.Done || consumedBytes + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }

            consumedChars += charsConsumed;
            consumedBytes += rune.Utf8SequenceLength;
            remaining = remaining[charsConsumed..];
        }

        return consumedChars == value.Length ? value : value[..consumedChars];
    }

    private static bool ExceedsUtf8Limit(string value, int maxBytes) =>
        value.Length > maxBytes || Encoding.UTF8.GetByteCount(value) > maxBytes;

    private static void ZeroVariants(IEnumerable<byte[]> variants)
    {
        foreach (var variant in variants)
        {
            CryptographicOperations.ZeroMemory(variant);
        }
    }

    private static void AddDistinct(List<byte[]> variants, byte[] candidate, int maxVariants)
    {
        byte[]? ownedCandidate = candidate;
        try
        {
            if (variants.Count >= maxVariants ||
                candidate.Length < MinimumSecretBytes ||
                variants.Any(existing => existing.AsSpan().SequenceEqual(candidate)))
            {
                return;
            }

            variants.Add(candidate);
            ownedCandidate = null;
        }
        finally
        {
            if (ownedCandidate is not null)
            {
                CryptographicOperations.ZeroMemory(ownedCandidate);
            }
        }
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
