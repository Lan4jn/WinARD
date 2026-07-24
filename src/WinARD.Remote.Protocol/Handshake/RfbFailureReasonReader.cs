using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Handshake;

internal static class RfbFailureReasonReader
{
    private const int MaximumDisplayedReasonLength = 4096;
    private const int MaximumRedactedReasonBytes = MaximumDisplayedReasonLength * 4;

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
        RedactedReason redacted = default;
        try
        {
            redacted = Redact(reasonBytes, firstSecret.Span, secondSecret.Span);
            var safeReason = CreateSafeDisplayedReason(
                Encoding.UTF8.GetString(redacted.Bytes.AsSpan(0, redacted.Length)),
                redacted.IsTruncated);
            return ContainsSecretText(safeReason.Reason, firstSecret.Span, secondSecret.Span)
                ? new RfbFailureReason(string.Empty, false)
                : safeReason;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(reasonBytes);
            if (redacted.Bytes is not null)
            {
                CryptographicOperations.ZeroMemory(redacted.Bytes);
            }
        }
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

    private static bool ContainsSecretText(
        string reason,
        ReadOnlySpan<byte> firstSecret,
        ReadOnlySpan<byte> secondSecret) =>
        ContainsSecretText(reason, firstSecret) || ContainsSecretText(reason, secondSecret);

    private static bool ContainsSecretText(string reason, ReadOnlySpan<byte> secret)
    {
        if (secret.IsEmpty)
        {
            return false;
        }

        var characters = new char[Encoding.UTF8.GetCharCount(secret)];
        try
        {
            _ = Encoding.UTF8.GetChars(secret, characters);
            return reason.AsSpan().IndexOf(characters, StringComparison.Ordinal) >= 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }

    private static RfbFailureReason CreateSafeDisplayedReason(string decodedReason, bool isInputTruncated)
    {
        var builder = new StringBuilder(Math.Min(decodedReason.Length, MaximumDisplayedReasonLength));
        var runeCount = 0;
        foreach (var rune in decodedReason.EnumerateRunes())
        {
            if (runeCount == MaximumDisplayedReasonLength)
            {
                return new RfbFailureReason(builder.ToString(), true);
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

        return new RfbFailureReason(builder.ToString(), isInputTruncated);
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
