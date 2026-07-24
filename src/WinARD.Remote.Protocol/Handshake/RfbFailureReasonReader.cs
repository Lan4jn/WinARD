using System.Text;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Handshake;

internal static class RfbFailureReasonReader
{
    private const int MaximumDisplayedReasonLength = 4096;

    public static async ValueTask<RfbFailureReason> ReadAsync(
        RfbReader reader,
        ProtocolLimits limits,
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
        return CreateSafeDisplayedReason(Encoding.UTF8.GetString(reasonBytes));
    }

    private static RfbFailureReason CreateSafeDisplayedReason(string decodedReason)
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

        return new RfbFailureReason(builder.ToString(), false);
    }

    private static bool IsUnsafeDisplayRune(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is
            System.Globalization.UnicodeCategory.Control or
            System.Globalization.UnicodeCategory.Format or
            System.Globalization.UnicodeCategory.LineSeparator or
            System.Globalization.UnicodeCategory.ParagraphSeparator;
}

internal sealed record RfbFailureReason(string Reason, bool IsTruncated);
