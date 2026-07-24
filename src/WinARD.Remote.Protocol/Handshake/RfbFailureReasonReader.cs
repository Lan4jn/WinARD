using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Handshake;

internal static class RfbFailureReasonReader
{
    private const int MaximumDisplayedReasonLength = 4096;
    private const int MaximumRedactedReasonBytes = MaximumDisplayedReasonLength * 4;
    private static readonly AsyncLocal<Action?> MaterializationObserverStorage = new();

    internal static Action? MaterializationObserver
    {
        get => MaterializationObserverStorage.Value;
        set => MaterializationObserverStorage.Value = value;
    }

    public static ValueTask<RfbFailureReason> ReadAsync(
        RfbReader reader,
        ProtocolLimits limits,
        CancellationToken cancellationToken) =>
        ReadAsync(reader, limits, default, default, cancellationToken);

    public static async ValueTask<RfbFailureReason> ReadAsync(
        RfbReader reader,
        ProtocolLimits limits,
        ReadOnlyMemory<byte> firstSecret,
        ReadOnlyMemory<byte> secondSecret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(limits);

        var reasonLength = await reader.ReadUInt32Async(cancellationToken);
        if (reasonLength > (uint)limits.MaxMessageBytes)
        {
            throw new RfbProtocolException(
                $"Server failure reason length {reasonLength} exceeds the configured limit of {limits.MaxMessageBytes} bytes.");
        }

        var reasonBytes = await reader.ReadBytesAsync((int)reasonLength, cancellationToken);
        try
        {
            return ProcessReason(reasonBytes, firstSecret, secondSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(reasonBytes);
        }
    }

    private static RfbFailureReason ProcessReason(
        byte[] reasonBytes,
        ReadOnlyMemory<byte> firstSecret,
        ReadOnlyMemory<byte> secondSecret)
    {
        RedactedReason redacted = default;
        char[]? decodedCharacters = null;
        char[]? safeCharacters = null;
        try
        {
            if (ContainsNonAscii(firstSecret.Span) || ContainsNonAscii(secondSecret.Span))
            {
                return new RfbFailureReason(string.Empty, false);
            }

            redacted = Redact(reasonBytes, firstSecret.Span, secondSecret.Span);
            var redactedBytes = redacted.Bytes.AsSpan(0, redacted.Length);
            if (ContainsSecretBytes(redactedBytes, firstSecret.Span, secondSecret.Span))
            {
                return new RfbFailureReason(string.Empty, false);
            }

            var decodedLength = Encoding.UTF8.GetCharCount(redactedBytes);
            decodedCharacters = ArrayPool<char>.Shared.Rent(Math.Max(decodedLength, 1));
            decodedLength = Encoding.UTF8.GetChars(redactedBytes, decodedCharacters);

            safeCharacters = ArrayPool<char>.Shared.Rent(MaximumDisplayedReasonLength);
            var safeLength = Sanitize(
                decodedCharacters.AsSpan(0, decodedLength),
                safeCharacters,
                out var isDisplayTruncated);
            var safeSpan = safeCharacters.AsSpan(0, safeLength);
            if (ContainsSecretCharacters(safeSpan, firstSecret.Span, secondSecret.Span))
            {
                return new RfbFailureReason(string.Empty, false);
            }

            MaterializationObserver?.Invoke();
            return new RfbFailureReason(
                new string(safeCharacters, 0, safeLength),
                redacted.IsTruncated || isDisplayTruncated);
        }
        finally
        {
            if (redacted.Bytes is not null)
            {
                CryptographicOperations.ZeroMemory(redacted.Bytes);
            }

            if (decodedCharacters is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(decodedCharacters.AsSpan()));
                ArrayPool<char>.Shared.Return(decodedCharacters);
            }

            if (safeCharacters is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(safeCharacters.AsSpan()));
                ArrayPool<char>.Shared.Return(safeCharacters);
            }
        }
    }

    private static bool ContainsNonAscii(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
        {
            if (item > 0x7f)
            {
                return true;
            }
        }

        return false;
    }

    private static RedactedReason Redact(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> firstSecret,
        ReadOnlySpan<byte> secondSecret)
    {
        ReadOnlySpan<byte> replacement = "[REDACTED]"u8;
        var output = source.IsEmpty ? Array.Empty<byte>() : new byte[MaximumRedactedReasonBytes];
        var outputOffset = 0;
        for (var index = 0; index < source.Length;)
        {
            var matchLength = GetLongestMatch(source[index..], firstSecret, secondSecret);
            if (matchLength == 0)
            {
                if (outputOffset == output.Length)
                {
                    return new RedactedReason(output, outputOffset, true);
                }

                output[outputOffset++] = source[index++];
                continue;
            }

            if (replacement.Length > output.Length - outputOffset)
            {
                return new RedactedReason(output, outputOffset, true);
            }

            replacement.CopyTo(output.AsSpan(outputOffset));
            outputOffset += replacement.Length;
            index += matchLength;
        }

        return new RedactedReason(output, outputOffset, false);
    }

    private static int GetLongestMatch(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> firstSecret,
        ReadOnlySpan<byte> secondSecret)
    {
        var matchLength = !firstSecret.IsEmpty && source.StartsWith(firstSecret) ? firstSecret.Length : 0;
        if (!secondSecret.IsEmpty && source.StartsWith(secondSecret))
        {
            matchLength = Math.Max(matchLength, secondSecret.Length);
        }

        return matchLength;
    }

    private static bool ContainsSecretBytes(
        ReadOnlySpan<byte> reason,
        ReadOnlySpan<byte> firstSecret,
        ReadOnlySpan<byte> secondSecret) =>
        ContainsSecretBytes(reason, firstSecret) || ContainsSecretBytes(reason, secondSecret);

    private static bool ContainsSecretBytes(ReadOnlySpan<byte> reason, ReadOnlySpan<byte> secret) =>
        !secret.IsEmpty && reason.IndexOf(secret) >= 0;

    private static bool ContainsSecretCharacters(
        ReadOnlySpan<char> reason,
        ReadOnlySpan<byte> firstSecret,
        ReadOnlySpan<byte> secondSecret) =>
        ContainsSecretCharacters(reason, firstSecret) || ContainsSecretCharacters(reason, secondSecret);

    private static bool ContainsSecretCharacters(ReadOnlySpan<char> reason, ReadOnlySpan<byte> secret)
    {
        if (secret.IsEmpty || secret.Length > reason.Length)
        {
            return false;
        }

        for (var start = 0; start <= reason.Length - secret.Length; start++)
        {
            var matches = true;
            for (var index = 0; index < secret.Length; index++)
            {
                if (reason[start + index] != (char)secret[index])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static int Sanitize(
        ReadOnlySpan<char> source,
        Span<char> destination,
        out bool isTruncated)
    {
        var sourceOffset = 0;
        var destinationOffset = 0;
        while (sourceOffset < source.Length)
        {
            var status = Rune.DecodeFromUtf16(source[sourceOffset..], out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                consumed = 1;
            }

            var requiredLength = IsUnsafeDisplayRune(rune)
                ? 2 + GetHexDigitCount(rune.Value)
                : rune.Utf16SequenceLength;
            if (requiredLength > destination.Length - destinationOffset)
            {
                isTruncated = true;
                return destinationOffset;
            }

            if (IsUnsafeDisplayRune(rune))
            {
                destination[destinationOffset++] = '\\';
                destination[destinationOffset++] = 'u';
                WriteHex(rune.Value, destination.Slice(destinationOffset, requiredLength - 2));
                destinationOffset += requiredLength - 2;
            }
            else
            {
                destinationOffset += rune.EncodeToUtf16(destination[destinationOffset..]);
            }

            sourceOffset += consumed;
        }

        isTruncated = false;
        return destinationOffset;
    }

    private static int GetHexDigitCount(int value)
    {
        var count = 4;
        while (value >= 0x10000)
        {
            count++;
            value >>= 4;
        }

        return count;
    }

    private static void WriteHex(int value, Span<char> destination)
    {
        const string digits = "0123456789ABCDEF";
        for (var index = destination.Length - 1; index >= 0; index--)
        {
            destination[index] = digits[value & 0xf];
            value >>= 4;
        }
    }

    private static bool IsUnsafeDisplayRune(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is
            System.Globalization.UnicodeCategory.Control or
            System.Globalization.UnicodeCategory.Format or
            System.Globalization.UnicodeCategory.LineSeparator or
            System.Globalization.UnicodeCategory.ParagraphSeparator;

    private readonly record struct RedactedReason(byte[] Bytes, int Length, bool IsTruncated);
}

internal sealed record RfbFailureReason(string Reason, bool IsTruncated);
