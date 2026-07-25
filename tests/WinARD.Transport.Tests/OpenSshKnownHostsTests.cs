using WinARD.Domain.Connections;
using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Transport.Tests;

public sealed class OpenSshKnownHostsTests
{
    [Fact]
    public void Formats_only_the_endpoint_bound_raw_public_key()
    {
        var pin = SshHostKeyVerifier.CreateCandidate(
            new SshHostKeyEndpoint("2001:db8::1", 2222),
            "ssh-ed25519",
            Convert.ToBase64String([1, 2, 3, 4])).ToPin();

        var text = OpenSshKnownHosts.Format(pin);

        Assert.Equal(
            $"[2001:db8::1]:2222 ssh-ed25519 {Convert.ToBase64String([1, 2, 3, 4])}{Environment.NewLine}",
            text);
        Assert.DoesNotContain(pin.Fingerprint, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Formats_default_port_using_the_OpenSSH_default_host_syntax()
    {
        var pin = SshHostKeyVerifier.CreateCandidate(
            new SshHostKeyEndpoint("jump.example", 22),
            "ssh-ed25519",
            Convert.ToBase64String([1, 2, 3, 4])).ToPin();

        var text = OpenSshKnownHosts.Format(pin);

        Assert.Equal(
            $"jump.example ssh-ed25519 {Convert.ToBase64String([1, 2, 3, 4])}{Environment.NewLine}",
            text);
    }

    [Fact]
    public void Parses_keyscan_output_and_computes_the_fingerprint_independently()
    {
        var endpoint = new SshHostKeyEndpoint("jump.example", 2222);
        var rawKey = new byte[] { 9, 8, 7, 6 };
        var output = $"""
            # jump.example:2222 SSH-2.0-OpenSSH
            [jump.example]:2222 ssh-ed25519 {Convert.ToBase64String(rawKey)}

            """;

        var candidates = OpenSshKeyScanParser.Parse(output, endpoint);

        var candidate = Assert.Single(candidates);
        Assert.Equal(endpoint, candidate.Endpoint);
        Assert.Equal("ssh-ed25519", candidate.Algorithm);
        Assert.Equal(
            SshHostKeyVerifier.ComputeFingerprint(rawKey),
            candidate.Fingerprint);
    }

    [Fact]
    public void Rejects_keyscan_output_for_a_different_endpoint()
    {
        var endpoint = new SshHostKeyEndpoint("jump.example", 2222);
        var output = $"[other.example]:2222 ssh-ed25519 {Convert.ToBase64String([1, 2, 3])}";

        Assert.Throws<OpenSshKeyScanException>(
            () => OpenSshKeyScanParser.Parse(output, endpoint));
    }
}
