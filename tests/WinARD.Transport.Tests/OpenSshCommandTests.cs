using WinARD.Domain.Connections;
using WinARD.Domain.Security;
using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Transport.Tests;

public sealed class OpenSshCommandTests
{
    [Fact]
    public void Tunnel_arguments_are_strict_structured_tokens()
    {
        var profile = SshProfile.Create(
            "jump.example",
            2222,
            "ssh-user",
            privateKeyPath: @"C:\Keys\mac key",
            targetHost: "2001:db8::10",
            targetPort: 5900,
            credentialReference: null,
            pinnedHostKeyAlgorithm: null,
            pinnedHostKeySha256: null);

        var start = OpenSshCommandBuilder.BuildTunnel(
            Path.GetFullPath(@"C:\Windows\System32\OpenSSH\ssh.exe"),
            profile,
            @"C:\Users\A User\AppData\Local\Temp\known hosts");

        Assert.Equal(
            Path.GetFullPath(@"C:\Windows\System32\OpenSSH\ssh.exe"),
            start.FileName);
        Assert.Equal(
            [
                "-T",
                "-W", "[2001:db8::10]:5900",
                "-p", "2222",
                "-F", "NUL",
                "-o", "BatchMode=yes",
                "-o", "StrictHostKeyChecking=yes",
                "-o", @"UserKnownHostsFile=C:\Users\A User\AppData\Local\Temp\known hosts",
                "-o", "GlobalKnownHostsFile=NUL",
                "-o", "UpdateHostKeys=no",
                "-o", "CheckHostIP=no",
                "-o", "IdentitiesOnly=yes",
                "-o", "PreferredAuthentications=publickey",
                "-i", @"C:\Keys\mac key",
                "ssh-user@jump.example",
            ],
            start.Arguments);
    }

    [Fact]
    public void Agent_authentication_does_not_add_an_identity_file()
    {
        var profile = CreateProfile("jump.example", "ssh-user");

        var start = OpenSshCommandBuilder.BuildTunnel(
            Path.GetFullPath(@"C:\Windows\System32\OpenSSH\ssh.exe"),
            profile,
            @"C:\Temp\known_hosts");

        Assert.DoesNotContain("-i", start.Arguments);
        Assert.Contains("IdentitiesOnly=no", start.Arguments);
        Assert.Contains("PreferredAuthentications=publickey", start.Arguments);
    }

    [Fact]
    public void Password_credential_forces_askpass_without_putting_secret_in_arguments()
    {
        var profile = SshProfile.Create(
            "jump.example",
            22,
            "ssh-user",
            privateKeyPath: null,
            targetHost: "127.0.0.1",
            targetPort: 5900,
            CredentialReference.Create("windows", "password-reference"),
            pinnedHostKeyAlgorithm: null,
            pinnedHostKeySha256: null);

        var start = OpenSshCommandBuilder.BuildTunnel(
            Path.GetFullPath(@"C:\Windows\System32\OpenSSH\ssh.exe"),
            profile,
            @"C:\Temp\known_hosts");

        Assert.Contains("BatchMode=no", start.Arguments);
        Assert.Contains(
            "PreferredAuthentications=password,keyboard-interactive",
            start.Arguments);
        Assert.DoesNotContain("password-reference", start.Arguments);
    }

    [Theory]
    [InlineData("jump.example\r\n-o StrictHostKeyChecking=no", "ssh-user")]
    [InlineData("-oProxyCommand=bad", "ssh-user")]
    [InlineData("jump.example", "ssh-user@other")]
    [InlineData("jump.example", "-malicious")]
    [InlineData("jump.example", "ssh\u0000user")]
    public void Rejects_host_or_username_argument_injection(string host, string username)
    {
        var profile = CreateProfile(host, username);

        Assert.Throws<ArgumentException>(
            () => OpenSshCommandBuilder.BuildTunnel(
                Path.GetFullPath(@"C:\Windows\System32\OpenSSH\ssh.exe"),
                profile,
                @"C:\Temp\known_hosts"));
    }

    [Fact]
    public void Keyscan_arguments_are_structured_and_bounded()
    {
        var endpoint = new SshHostKeyEndpoint("jump.example", 2222);

        var start = OpenSshCommandBuilder.BuildKeyScan(
            Path.GetFullPath(@"C:\Windows\System32\OpenSSH\ssh-keyscan.exe"),
            endpoint,
            TimeSpan.FromSeconds(7));

        Assert.Equal(
            Path.GetFullPath(@"C:\Windows\System32\OpenSSH\ssh-keyscan.exe"),
            start.FileName);
        Assert.Equal(["-T", "7", "-p", "2222", "jump.example"], start.Arguments);
    }

    private static SshProfile CreateProfile(string host, string username) =>
        SshProfile.Create(
            host,
            22,
            username,
            privateKeyPath: null,
            targetHost: "127.0.0.1",
            targetPort: 5900,
            credentialReference: null,
            pinnedHostKeyAlgorithm: null,
            pinnedHostKeySha256: null);
}
