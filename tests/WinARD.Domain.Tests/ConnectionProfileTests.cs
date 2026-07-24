using WinARD.Domain.Connections;
using WinARD.Domain.Devices;
using WinARD.Domain.Errors;
using WinARD.Domain.Security;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Domain.Tests;

public sealed class ConnectionProfileTests
{
    [Theory]
    [InlineData(" ", "mac.local", "alex", 5900)]
    [InlineData("Mac", " ", "alex", 5900)]
    [InlineData("Mac", "mac.local", " ", 5900)]
    [InlineData("Mac", "mac.local", "alex", 0)]
    [InlineData("Mac", "mac.local", "alex", 65536)]
    public void Create_rejects_invalid_values(string displayName, string host, string macUsername, int port)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            ConnectionProfile.Create(Guid.NewGuid(), displayName, host, port, macUsername));
    }

    [Fact]
    public void Create_rejects_empty_id()
    {
        Assert.Throws<ArgumentException>(() =>
            ConnectionProfile.Create(Guid.Empty, "Mac", "mac.local", 5900, "alex"));
    }

    [Fact]
    public void Create_trims_values_and_defaults_to_direct_transport()
    {
        var profile = ConnectionProfile.Create(Guid.NewGuid(), " Mac ", " mac.local ", 5900, " alex ");

        Assert.Equal("Mac", profile.DisplayName);
        Assert.Equal("mac.local", profile.Host);
        Assert.Equal("alex", profile.MacUsername);
        Assert.Equal(TransportMode.Direct, profile.TransportMode);
        Assert.Null(profile.CredentialReference);
        Assert.Null(profile.SshProfile);
    }

    [Fact]
    public void WithCredential_returns_new_value_and_preserves_connection_fields()
    {
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "alex");
        var reference = CredentialReference.Create("windows", "mac-device-1");

        var updated = profile.WithCredential(reference);

        Assert.NotSame(profile, updated);
        Assert.Equal(profile.Id, updated.Id);
        Assert.Equal(profile.DisplayName, updated.DisplayName);
        Assert.Equal(profile.Host, updated.Host);
        Assert.Equal(profile.Port, updated.Port);
        Assert.Equal(profile.MacUsername, updated.MacUsername);
        Assert.Equal(reference, updated.CredentialReference);
        Assert.Null(profile.CredentialReference);
    }

    [Fact]
    public void WithSsh_sets_ssh_transport_without_mutating_original()
    {
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "alex");
        var ssh = SshProfile.Create("bastion.local", 22, "jump", null, "mac.internal", 5900, null, " ", " ");

        var updated = profile.WithSsh(ssh);

        Assert.Equal(TransportMode.Ssh, updated.TransportMode);
        Assert.Equal(ssh, updated.SshProfile);
        Assert.Equal(TransportMode.Direct, profile.TransportMode);
        Assert.Null(profile.SshProfile);
        Assert.Null(ssh.PrivateKeyPath);
        Assert.Null(ssh.PinnedHostKeyAlgorithm);
        Assert.Null(ssh.PinnedHostKeySha256);
    }

    [Fact]
    public void WithoutSsh_restores_direct_transport()
    {
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "mac.local", 5900, "alex")
            .WithSsh(SshProfile.Create("bastion", 22, "jump", null, "mac", 5900, null, null, null));

        var direct = profile.WithoutSsh();

        Assert.Equal(TransportMode.Direct, direct.TransportMode);
        Assert.Null(direct.SshProfile);
    }

    [Fact]
    public void Device_create_validates_and_trims_values()
    {
        var created = DateTimeOffset.UtcNow;
        var device = Device.Create(Guid.NewGuid(), " Office Mac ", created, created);

        Assert.Equal("Office Mac", device.DisplayName);
        Assert.Throws<ArgumentException>(() => Device.Create(Guid.Empty, "Mac", created, created));
        Assert.Throws<ArgumentException>(() => Device.Create(Guid.NewGuid(), " ", created, created));
        Assert.Throws<ArgumentException>(() => Device.Create(Guid.NewGuid(), "Mac", created, created.AddTicks(-1)));
    }

    [Fact]
    public void SshProfile_create_validates_and_trims_values()
    {
        var ssh = SshProfile.Create(" bastion ", 22, " jump ", " key.pem ", " mac ", 5900, null, " ssh-ed25519 ", " SHA256:value ");

        Assert.Equal("bastion", ssh.Host);
        Assert.Equal("jump", ssh.Username);
        Assert.Equal("key.pem", ssh.PrivateKeyPath);
        Assert.Equal("mac", ssh.TargetHost);
        Assert.Equal("ssh-ed25519", ssh.PinnedHostKeyAlgorithm);
        Assert.Equal("SHA256:value", ssh.PinnedHostKeySha256);
        Assert.Throws<ArgumentException>(() => SshProfile.Create(" ", 22, "jump", null, "mac", 5900, null, null, null));
        Assert.ThrowsAny<ArgumentException>(() => SshProfile.Create("bastion", 0, "jump", null, "mac", 5900, null, null, null));
        Assert.Throws<ArgumentException>(() => SshProfile.Create("bastion", 22, " ", null, "mac", 5900, null, null, null));
        Assert.Throws<ArgumentException>(() => SshProfile.Create("bastion", 22, "jump", null, " ", 5900, null, null, null));
        Assert.ThrowsAny<ArgumentException>(() => SshProfile.Create("bastion", 22, "jump", null, "mac", 65536, null, null, null));
    }

    [Fact]
    public void WinArdError_rejects_blank_fields_and_trims_values()
    {
        var error = WinArdError.Create(ConnectionStage.Connecting, " timeout ", " Could not connect ", " corr-1 ");

        Assert.Equal("timeout", error.Code);
        Assert.Equal("Could not connect", error.UserMessage);
        Assert.Equal("corr-1", error.CorrelationId);
        Assert.Throws<ArgumentException>(() => WinArdError.Create(ConnectionStage.Connecting, " ", "Message", "id"));
    }
}
