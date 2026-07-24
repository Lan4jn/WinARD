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
        string? pinnedHostKeyAlgorithm,
        string? pinnedHostKeySha256)
    {
        Host = host;
        Port = port;
        Username = username;
        PrivateKeyPath = privateKeyPath;
        TargetHost = targetHost;
        TargetPort = targetPort;
        CredentialReference = credentialReference;
        PinnedHostKeyAlgorithm = pinnedHostKeyAlgorithm;
        PinnedHostKeySha256 = pinnedHostKeySha256;
    }

    public string Host { get; }

    public int Port { get; }

    public string Username { get; }

    public string? PrivateKeyPath { get; }

    public string TargetHost { get; }

    public int TargetPort { get; }

    public CredentialReference? CredentialReference { get; }

    public string? PinnedHostKeyAlgorithm { get; }

    public string? PinnedHostKeySha256 { get; }

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
        return new SshProfile(
            RequiredTrimmed(host, nameof(host)),
            ValidPort(port, nameof(port)),
            RequiredTrimmed(username, nameof(username)),
            OptionalTrimmed(privateKeyPath),
            RequiredTrimmed(targetHost, nameof(targetHost)),
            ValidPort(targetPort, nameof(targetPort)),
            credentialReference,
            OptionalTrimmed(pinnedHostKeyAlgorithm),
            OptionalTrimmed(pinnedHostKeySha256));
    }

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
