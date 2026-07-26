using WinARD.Domain.Security;

namespace WinARD.Domain.Connections;

public sealed record SshProfile
{
    private SshProfile(
        string host,
        int port,
        string username,
        string? privateKeyPath,
        string targetHost,
        int targetPort,
        CredentialReference? credentialReference,
        CredentialReference? passwordCredentialReference,
        CredentialReference? privateKeyPassphraseCredentialReference,
        string? pinnedHostKeyAlgorithm,
        string? pinnedHostKeySha256,
        SshHostKeyPin? hostKeyPin)
    {
        Host = host;
        Port = port;
        Username = username;
        PrivateKeyPath = privateKeyPath;
        TargetHost = targetHost;
        TargetPort = targetPort;
        CredentialReference = credentialReference;
        PasswordCredentialReference = passwordCredentialReference;
        PrivateKeyPassphraseCredentialReference = privateKeyPassphraseCredentialReference;
        PinnedHostKeyAlgorithm = pinnedHostKeyAlgorithm;
        PinnedHostKeySha256 = pinnedHostKeySha256;
        HostKeyPin = hostKeyPin;
    }

    public string Host { get; }

    public int Port { get; }

    public string Username { get; }

    public string? PrivateKeyPath { get; }

    public string TargetHost { get; }

    public int TargetPort { get; }

    public CredentialReference? CredentialReference { get; }

    public CredentialReference? PasswordCredentialReference { get; }

    public CredentialReference? PrivateKeyPassphraseCredentialReference { get; }

    public string? PinnedHostKeyAlgorithm { get; }

    public string? PinnedHostKeySha256 { get; }

    public SshHostKeyPin? HostKeyPin { get; }

    public static SshProfile Create(
        string host,
        int port,
        string username,
        string? privateKeyPath,
        string targetHost,
        int targetPort,
        CredentialReference? credentialReference,
        string? pinnedHostKeyAlgorithm,
        string? pinnedHostKeySha256)
    {
        var normalizedPrivateKeyPath = OptionalTrimmed(privateKeyPath);
        var normalizedPinnedHostKeyAlgorithm = OptionalTrimmed(pinnedHostKeyAlgorithm);
        var normalizedPinnedHostKeySha256 = OptionalTrimmed(pinnedHostKeySha256);
        if ((normalizedPinnedHostKeyAlgorithm is null) != (normalizedPinnedHostKeySha256 is null))
        {
            throw new ArgumentException(
                "Pinned host key algorithm and SHA-256 fingerprint must be specified together.",
                nameof(pinnedHostKeySha256));
        }

        return new SshProfile(
            RequiredTrimmed(host, nameof(host)),
            ValidPort(port, nameof(port)),
            RequiredTrimmed(username, nameof(username)),
            normalizedPrivateKeyPath,
            RequiredTrimmed(targetHost, nameof(targetHost)),
            ValidPort(targetPort, nameof(targetPort)),
            credentialReference,
            normalizedPrivateKeyPath is null ? credentialReference : null,
            normalizedPrivateKeyPath is null ? null : credentialReference,
            normalizedPinnedHostKeyAlgorithm,
            normalizedPinnedHostKeySha256,
            hostKeyPin: null);
    }

    public SshProfile WithHostKeyPin(SshHostKeyPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return new SshProfile(
            Host,
            Port,
            Username,
            PrivateKeyPath,
            TargetHost,
            TargetPort,
            CredentialReference,
            PasswordCredentialReference,
            PrivateKeyPassphraseCredentialReference,
            pin.Algorithm,
            pin.Fingerprint,
            pin);
    }

    public SshProfile WithEndpoint(string host, int port) =>
        new(
            RequiredTrimmed(host, nameof(host)),
            ValidPort(port, nameof(port)),
            Username,
            PrivateKeyPath,
            TargetHost,
            TargetPort,
            CredentialReference,
            PasswordCredentialReference,
            PrivateKeyPassphraseCredentialReference,
            PinnedHostKeyAlgorithm,
            PinnedHostKeySha256,
            HostKeyPin);

    public SshProfile WithAuthenticationCredentials(
        CredentialReference? password,
        CredentialReference? privateKeyPassphrase) =>
        new(
            Host,
            Port,
            Username,
            PrivateKeyPath,
            TargetHost,
            TargetPort,
            password ?? privateKeyPassphrase,
            password,
            privateKeyPassphrase,
            PinnedHostKeyAlgorithm,
            PinnedHostKeySha256,
            HostKeyPin);

    private static string RequiredTrimmed(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be blank.", parameterName);
        }

        return value.Trim();
    }

    private static string? OptionalTrimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ValidPort(int value, string parameterName)
    {
        if (value is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Port must be between 1 and 65535.");
        }

        return value;
    }
}
