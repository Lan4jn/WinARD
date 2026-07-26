using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using WinARD.Domain.Connections;

namespace WinARD.Transport.Ssh;

public sealed record SshHostKeyCandidate(
    SshHostKeyEndpoint Endpoint,
    string Algorithm,
    string PublicKeyBase64,
    string Fingerprint)
{
    public SshHostKeyPin ToPin() =>
        new(Endpoint, Algorithm, PublicKeyBase64, Fingerprint);
}

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
    string PublicKeyBase64,
    string Fingerprint)
{
    public SshHostKeyPin ToPin() =>
        new(Endpoint, Algorithm, PublicKeyBase64, Fingerprint);
}

public sealed class SshHostKeyVerifier
{
    public static SshHostKeyCandidate CreateCandidate(
        SshHostKeyEndpoint endpoint,
        string algorithm,
        string publicKeyBase64)
    {
        var canonicalAlgorithm = CanonicalAlgorithm(algorithm);
        byte[] rawKey;
        try
        {
            rawKey = Convert.FromBase64String(publicKeyBase64.Trim());
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("SSH public key must be valid base64.", nameof(publicKeyBase64), exception);
        }

        try
        {
            return new SshHostKeyCandidate(
                endpoint,
                canonicalAlgorithm,
                Convert.ToBase64String(rawKey),
                ComputeFingerprint(rawKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawKey);
        }
    }

    public static SshHostKeyVerification Verify(
        SshHostKeyCandidate candidate,
        SshHostKeyPin? pin)
    {
        if (pin is null || pin.Endpoint != candidate.Endpoint)
        {
            return ToVerification(SshHostKeyStatus.Unknown, candidate);
        }

        var matches =
            FixedTimeEquals(candidate.Algorithm, CanonicalAlgorithm(pin.Algorithm)) &&
            FixedTimeEquals(candidate.PublicKeyBase64, CanonicalPublicKey(pin.PublicKeyBase64)) &&
            FixedTimeEquals(candidate.Fingerprint, CanonicalFingerprint(pin.Fingerprint));

        return ToVerification(
            matches ? SshHostKeyStatus.Trusted : SshHostKeyStatus.Changed,
            candidate);
    }

    public static SshHostKeyVerification Verify(
        SshHostKeyEndpoint endpoint,
        string algorithm,
        ReadOnlySpan<byte> hostKey,
        SshHostKeyPin? pin) =>
        Verify(
            CreateCandidate(endpoint, algorithm, Convert.ToBase64String(hostKey)),
            pin);

    public static SshHostKeyPin Confirm(SshHostKeyVerification verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (verification.Status == SshHostKeyStatus.Changed)
        {
            throw new SshHostKeyChangedException(verification);
        }

        return verification.ToPin();
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

    private static SshHostKeyVerification ToVerification(
        SshHostKeyStatus status,
        SshHostKeyCandidate candidate) =>
        new(
            status,
            candidate.Endpoint,
            candidate.Algorithm,
            candidate.PublicKeyBase64,
            candidate.Fingerprint);

    private static string CanonicalAlgorithm(string algorithm)
    {
        if (string.IsNullOrWhiteSpace(algorithm))
        {
            throw new ArgumentException("Host key algorithm cannot be blank.", nameof(algorithm));
        }

        return algorithm.Trim().ToLowerInvariant();
    }

    private static string CanonicalPublicKey(string publicKeyBase64)
    {
        byte[] rawKey;
        try
        {
            rawKey = Convert.FromBase64String(publicKeyBase64.Trim());
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("SSH public key must be valid base64.", nameof(publicKeyBase64), exception);
        }

        try
        {
            return Convert.ToBase64String(rawKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawKey);
        }
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

public interface ISshHostKeyPinStore
{
    ValueTask<SshHostKeyPin?> FindAsync(
        SshHostKeyEndpoint endpoint,
        CancellationToken cancellationToken);

    ValueTask<SshHostKeyPinConfirmation> ConfirmUnknownAsync(
        SshHostKeyPin pin,
        CancellationToken cancellationToken);
}

public enum SshHostKeyPinConfirmation
{
    Stored,
    AlreadyConfirmed,
    Conflict,
}

public sealed class InMemorySshHostKeyPinStore : ISshHostKeyPinStore
{
    private readonly ConcurrentDictionary<SshHostKeyEndpoint, SshHostKeyPin> _pins = new();

    public ValueTask<SshHostKeyPin?> FindAsync(
        SshHostKeyEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pins.TryGetValue(endpoint, out var pin);
        return ValueTask.FromResult(pin);
    }

    public ValueTask<SshHostKeyPinConfirmation> ConfirmUnknownAsync(
        SshHostKeyPin pin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pin);
        cancellationToken.ThrowIfCancellationRequested();
        if (_pins.TryAdd(pin.Endpoint, pin))
        {
            return ValueTask.FromResult(SshHostKeyPinConfirmation.Stored);
        }

        var existing = _pins[pin.Endpoint];
        return ValueTask.FromResult(
            existing == pin
                ? SshHostKeyPinConfirmation.AlreadyConfirmed
                : SshHostKeyPinConfirmation.Conflict);
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

    public SshHostKeyChangedException(SshHostKeyVerification verification)
        : base($"The SSH host key for {verification.Endpoint.Host}:{verification.Endpoint.Port} has changed.")
    {
        Verification = verification ?? throw new ArgumentNullException(nameof(verification));
        Endpoint = verification.Endpoint;
    }

    public SshHostKeyEndpoint Endpoint { get; }

    public SshHostKeyVerification? Verification { get; }
}
