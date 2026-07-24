using System.Globalization;
using System.Text;
using WinARD.Remote.Protocol.Errors;

namespace WinARD.Remote.Protocol.Handshake;

#pragma warning disable CA1707

public sealed record RfbVersion
{
    private RfbVersion(int major, int minor, string banner)
    {
        Major = major;
        Minor = minor;
        Banner = banner;
    }

    public static RfbVersion V3_3 { get; } = new(3, 3, "RFB 003.003\n");

    public static RfbVersion V3_7 { get; } = new(3, 7, "RFB 003.007\n");

    public static RfbVersion V3_8 { get; } = new(3, 8, "RFB 003.008\n");

    public int Major { get; }

    public int Minor { get; }

    public string Banner { get; }

    internal static RfbVersion Parse(ReadOnlySpan<byte> banner)
    {
        if (banner.Length != 12)
        {
            throw new RfbProtocolException("The RFB version banner must be exactly 12 bytes.");
        }

        if (banner[0] != (byte)'R' ||
            banner[1] != (byte)'F' ||
            banner[2] != (byte)'B' ||
            banner[3] != (byte)' ' ||
            banner[7] != (byte)'.' ||
            banner[11] != (byte)'\n' ||
            !AreAsciiDigits(banner[4..7]) ||
            !AreAsciiDigits(banner[8..11]))
        {
            throw new RfbProtocolException("The server sent a malformed RFB version banner.");
        }

        var major = ParseThreeDigits(banner[4..7]);
        var minor = ParseThreeDigits(banner[8..11]);
        return (major, minor) switch
        {
            (3, 3) => V3_3,
            (3, 7) => V3_7,
            (3, 8) => V3_8,
            (3, 889) => V3_8,
            _ => throw new UnsupportedRfbVersionException(ToSafeBannerText(banner)),
        };
    }

    private static bool AreAsciiDigits(ReadOnlySpan<byte> value) =>
        value[0] is >= (byte)'0' and <= (byte)'9' &&
        value[1] is >= (byte)'0' and <= (byte)'9' &&
        value[2] is >= (byte)'0' and <= (byte)'9';

    private static int ParseThreeDigits(ReadOnlySpan<byte> value) =>
        ((value[0] - (byte)'0') * 100) + ((value[1] - (byte)'0') * 10) + (value[2] - (byte)'0');

    private static string ToSafeBannerText(ReadOnlySpan<byte> banner)
    {
        var builder = new StringBuilder(banner.Length * 2);
        foreach (var value in banner)
        {
            if (value is >= 0x20 and <= 0x7e)
            {
                builder.Append((char)value);
            }
            else
            {
                builder.Append("\\x");
                builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
