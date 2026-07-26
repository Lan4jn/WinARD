using System.Globalization;
using System.Net;

namespace WinARD.Infrastructure.Devices;

internal static class HostCanonicalizer
{
    public static string Canonicalize(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host cannot be blank.", nameof(host));
        }

        var trimmed = host.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1];
        }

        if (trimmed.Length > 253)
        {
            throw new ArgumentException("Host is too long.", nameof(host));
        }

        if (IPAddress.TryParse(trimmed, out var address))
        {
            return address.ToString().ToLowerInvariant();
        }

        var ascii = new IdnMapping().GetAscii(trimmed.TrimEnd('.'));
        if (ascii.Length > 253 || Uri.CheckHostName(ascii) != UriHostNameType.Dns)
        {
            throw new ArgumentException("Host must be a valid DNS name or IP address.", nameof(host));
        }

        return ascii.ToLowerInvariant();
    }
}
