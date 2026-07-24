using WinARD.Domain.Connections;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Domain.Tests;

public sealed class SshProfileTests
{
    [Fact]
    public void Create_validates_and_trims_values()
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
    public void Create_rejects_pinned_algorithm_without_fingerprint()
    {
        Assert.Throws<ArgumentException>(() => SshProfile.Create("bastion", 22, "jump", null, "mac", 5900, null, "ssh-ed25519", null));
    }

    [Fact]
    public void Create_rejects_pinned_fingerprint_without_algorithm()
    {
        Assert.Throws<ArgumentException>(() => SshProfile.Create("bastion", 22, "jump", null, "mac", 5900, null, null, "SHA256:value"));
    }

    [Fact]
    public void Create_normalizes_blank_pinned_values_to_null()
    {
        var ssh = SshProfile.Create("bastion", 22, "jump", null, "mac", 5900, null, " ", " ");

        Assert.Null(ssh.PinnedHostKeyAlgorithm);
        Assert.Null(ssh.PinnedHostKeySha256);
    }

    [Fact]
    public void Create_accepts_complete_pinned_host_key_values()
    {
        var ssh = SshProfile.Create("bastion", 22, "jump", null, "mac", 5900, null, "ssh-ed25519", "SHA256:value");

        Assert.Equal("ssh-ed25519", ssh.PinnedHostKeyAlgorithm);
        Assert.Equal("SHA256:value", ssh.PinnedHostKeySha256);
    }
}
