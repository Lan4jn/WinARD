using WinARD.Domain.Security;

namespace WinARD.Domain.Connections;

public sealed record ConnectionProfile
{
    private ConnectionProfile(
        Guid id,
        string displayName,
        string host,
        int port,
        string macUsername,
        TransportMode transportMode,
        CredentialReference? credentialReference,
        SshProfile? sshProfile,
        QualityProfile quality)
    {
        Id = id;
        DisplayName = displayName;
        Host = host;
        Port = port;
        MacUsername = macUsername;
        TransportMode = transportMode;
        CredentialReference = credentialReference;
        SshProfile = sshProfile;
        Quality = quality;
    }

    public Guid Id { get; }

    public string DisplayName { get; }

    public string Host { get; }

    public int Port { get; }

    public string MacUsername { get; }

    public TransportMode TransportMode { get; }

    public CredentialReference? CredentialReference { get; }

    public SshProfile? SshProfile { get; }

    public QualityProfile Quality { get; }

    public FrameRefreshPolicy FrameRefreshPolicy => Quality.Refresh;

    public static ConnectionProfile Create(Guid id, string displayName, string host, int port, string macUsername)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Connection profile ID cannot be empty.", nameof(id));
        }

        return new ConnectionProfile(
            id,
            RequiredTrimmed(displayName, nameof(displayName)),
            RequiredTrimmed(host, nameof(host)),
            ValidPort(port, nameof(port)),
            RequiredTrimmed(macUsername, nameof(macUsername)),
            TransportMode.Direct,
            null,
            null,
            QualityProfile.Automatic);
    }

    public ConnectionProfile WithCredential(CredentialReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new ConnectionProfile(
            Id,
            DisplayName,
            Host,
            Port,
            MacUsername,
            TransportMode,
            reference,
            SshProfile,
            Quality);
    }

    public ConnectionProfile WithSsh(SshProfile sshProfile)
    {
        ArgumentNullException.ThrowIfNull(sshProfile);
        return new ConnectionProfile(
            Id,
            DisplayName,
            Host,
            Port,
            MacUsername,
            TransportMode.Ssh,
            CredentialReference,
            sshProfile,
            Quality);
    }

    public ConnectionProfile WithoutSsh() =>
        new(Id, DisplayName, Host, Port, MacUsername, TransportMode.Direct, CredentialReference, null, Quality);

    public ConnectionProfile WithFrameRefreshPolicy(FrameRefreshPolicy policy) =>
        new(Id, DisplayName, Host, Port, MacUsername, TransportMode, CredentialReference, SshProfile, Quality.WithRefresh(policy));

    public ConnectionProfile WithQualityProfile(QualityProfile quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        return new(Id, DisplayName, Host, Port, MacUsername, TransportMode, CredentialReference, SshProfile, quality);
    }

    private static string RequiredTrimmed(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be blank.", parameterName);
        }

        return value.Trim();
    }

    private static int ValidPort(int value, string parameterName)
    {
        if (value is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Port must be between 1 and 65535.");
        }

        return value;
    }
}
