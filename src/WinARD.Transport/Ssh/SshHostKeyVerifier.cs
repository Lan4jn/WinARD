using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace WinARD.Transport.Ssh;

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

public sealed record SshHostKeyPin(
    SshHostKeyEndpoint Endpoint,
    string Algorithm,
    string Fingerprint);

public enum SshHostKeyStatus
{
    Trusted,
    Unknown,
    Changed,
}

public sealed record SshHostKeyVerification(
    SshHostKeyStatus Status,
    SshHostKeyEndpoint Endpoint,
    string Algorithm,
    string Fingerprint);

public sealed class SshHostKeyVerifier
{
    public static SshHostKeyVerification Verify(
        SshHostKeyEndpoint endpoint,
        string algorithm,
        ReadOnlySpan<byte> hostKey,
        SshHostKeyPin? pin)
    {
        var canonicalAlgorithm = CanonicalAlgorithm(algorithm);
        var fingerprint = ComputeFingerprint(hostKey);
        if (pin is null)
        {
            return new SshHostKeyVerification(
                SshHostKeyStatus.Unknown,
                endpoint,
                canonicalAlgorithm,
                fingerprint);
        }

        var matches = endpoint == pin.Endpoint &&
            FixedTimeEquals(canonicalAlgorithm, CanonicalAlgorithm(pin.Algorithm)) &&
            FixedTimeEquals(fingerprint, CanonicalFingerprint(pin.Fingerprint));

        return new SshHostKeyVerification(
            matches ? SshHostKeyStatus.Trusted : SshHostKeyStatus.Changed,
            endpoint,
            canonicalAlgorithm,
            fingerprint);
    }

    public static SshHostKeyPin Confirm(SshHostKeyVerification verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (verification.Status == SshHostKeyStatus.Changed)
        {
            throw new SshHostKeyChangedException(verification.Endpoint);
        }

        return new SshHostKeyPin(
            verification.Endpoint,
            CanonicalAlgorithm(verification.Algorithm),
            CanonicalFingerprint(verification.Fingerprint));
    }

    public static string ComputeFingerprint(ReadOnlySpan<byte> hostKey)
    {
        var hash = SHA256.HashData(hostKey);
        try
        {
            return $"SHA256:{Convert.ToBase64String(hash).TrimEnd('=')}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static string CanonicalAlgorithm(string algorithm)
    {
        if (string.IsNullOrWhiteSpace(algorithm))
        {
            throw new ArgumentException("Host key algorithm cannot be blank.", nameof(algorithm));
        }

        return algorithm.Trim().ToLowerInvariant();
    }

    private static string CanonicalFingerprint(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("Host key fingerprint cannot be blank.", nameof(fingerprint));
        }

        var trimmed = fingerprint.Trim();
        return trimmed.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? $"SHA256:{trimmed[7..].TrimEnd('=')}"
            : $"SHA256:{trimmed.TrimEnd('=')}";
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        try
        {
            return leftBytes.Length == rightBytes.Length &&
                CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}

public sealed class SshHostKeyChangedException : Exception
{
    public SshHostKeyChangedException(SshHostKeyEndpoint endpoint)
        : this(endpoint, innerException: null)
    {
    }

    public SshHostKeyChangedException(
        SshHostKeyEndpoint endpoint,
        Exception? innerException)
        : base(
            $"The SSH host key for {endpoint.Host}:{endpoint.Port} has changed.",
            innerException)
    {
        Endpoint = endpoint;
    }

    public SshHostKeyEndpoint Endpoint { get; }
}
