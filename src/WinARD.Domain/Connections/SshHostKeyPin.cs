using System.Globalization;
using System.Net;

namespace WinARD.Domain.Connections;

public readonly record struct SshHostKeyEndpoint
{
    public SshHostKeyEndpoint(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("SSH host cannot be blank.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "SSH port must be between 1 and 65535.");
        }

        Host = CanonicalHost(host);
        Port = port;
    }

    public string Host { get; }

    public int Port { get; }

    private static string CanonicalHost(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1];
        }

        if (IPAddress.TryParse(trimmed, out var address))
        {
            return address.ToString().ToLowerInvariant();
        }

        var asciiHost = new IdnMapping().GetAscii(trimmed.TrimEnd('.'));
        if (Uri.CheckHostName(asciiHost) != UriHostNameType.Dns)
        {
            throw new ArgumentException("SSH host must be a valid DNS name or IP address.", nameof(host));
        }

        return asciiHost.ToLowerInvariant();
    }
}

public sealed record SshHostKeyPin
{
    public SshHostKeyPin(
        SshHostKeyEndpoint endpoint,
        string algorithm,
        string publicKeyBase64,
        string fingerprint)
    {
        Endpoint = endpoint;
        Algorithm = RequiredTrimmed(algorithm, nameof(algorithm)).ToLowerInvariant();
        PublicKeyBase64 = RequiredTrimmed(publicKeyBase64, nameof(publicKeyBase64));
        Fingerprint = RequiredTrimmed(fingerprint, nameof(fingerprint));
    }

    public SshHostKeyEndpoint Endpoint { get; }

    public string Algorithm { get; }

    public string PublicKeyBase64 { get; }

    public string Fingerprint { get; }

    private static string RequiredTrimmed(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be blank.", parameterName);
        }

        return value.Trim();
    }
}
