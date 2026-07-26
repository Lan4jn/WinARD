using System.Security.Cryptography;
using WinARD.Domain.Connections;
using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Transport.Tests;

public sealed class SshHostKeyVerifierTests
{
    [Fact]
    public void Unknown_key_returns_candidate_computed_from_raw_key_bytes()
    {
        var endpoint = new SshHostKeyEndpoint("EXAMPLE.com.", 22);
        byte[] hostKey = [1, 2, 3, 4, 5];

        var result = SshHostKeyVerifier.Verify(endpoint, " SSH-ED25519 ", hostKey, pin: null);

        Assert.Equal(SshHostKeyStatus.Unknown, result.Status);
        Assert.Equal("example.com", result.Endpoint.Host);
        Assert.Equal("ssh-ed25519", result.Algorithm);
        Assert.Equal($"SHA256:{Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=')}", result.Fingerprint);
    }

    [Fact]
    public void Confirmed_unknown_key_becomes_a_matching_endpoint_bound_pin()
    {
        var endpoint = new SshHostKeyEndpoint("mac.example", 2222);
        byte[] hostKey = [9, 8, 7, 6];
        var unknown = SshHostKeyVerifier.Verify(endpoint, "ssh-ed25519", hostKey, pin: null);

        var pin = SshHostKeyVerifier.Confirm(unknown);
        var trusted = SshHostKeyVerifier.Verify(endpoint, "SSH-ED25519", hostKey, pin);

        Assert.Equal(SshHostKeyStatus.Trusted, trusted.Status);
        Assert.Equal(endpoint, pin.Endpoint);
    }

    [Fact]
    public void Changed_key_is_blocked_and_cannot_be_confirmed_over_existing_pin()
    {
        var endpoint = new SshHostKeyEndpoint("mac.example", 22);
        var pin = SshHostKeyVerifier.Confirm(
            SshHostKeyVerifier.Verify(endpoint, "ssh-ed25519", [1, 2, 3], pin: null));

        var changed = SshHostKeyVerifier.Verify(endpoint, "ssh-ed25519", [3, 2, 1], pin);

        Assert.Equal(SshHostKeyStatus.Changed, changed.Status);
        var exception = Assert.Throws<SshHostKeyChangedException>(() => SshHostKeyVerifier.Confirm(changed));
        Assert.Equal(changed, exception.Verification);
    }

    [Fact]
    public void Algorithm_change_is_blocked_even_when_key_bytes_match()
    {
        var endpoint = new SshHostKeyEndpoint("mac.example", 22);
        byte[] hostKey = [42, 24];
        var pin = SshHostKeyVerifier.Confirm(
            SshHostKeyVerifier.Verify(endpoint, "ssh-ed25519", hostKey, pin: null));

        var changed = SshHostKeyVerifier.Verify(endpoint, "rsa-sha2-512", hostKey, pin);

        Assert.Equal(SshHostKeyStatus.Changed, changed.Status);
    }

    [Fact]
    public void Endpoint_key_canonicalizes_equivalent_ipv6_host_spelling()
    {
        byte[] hostKey = [11, 12, 13];
        var bracketed = new SshHostKeyEndpoint("[2001:db8::1]", 22);
        var unbracketed = new SshHostKeyEndpoint("2001:0db8:0:0:0:0:0:1", 22);
        var pin = SshHostKeyVerifier.Confirm(
            SshHostKeyVerifier.Verify(bracketed, "ssh-ed25519", hostKey, pin: null));

        var result = SshHostKeyVerifier.Verify(unbracketed, "ssh-ed25519", hostKey, pin);

        Assert.Equal(SshHostKeyStatus.Trusted, result.Status);
    }

    [Fact]
    public void Same_public_key_at_a_different_host_requires_confirmation()
    {
        var originalEndpoint = new SshHostKeyEndpoint("old.example", 22);
        var editedEndpoint = new SshHostKeyEndpoint("new.example", 22);
        var pin = SshHostKeyVerifier.CreateCandidate(
            originalEndpoint,
            "ssh-ed25519",
            Convert.ToBase64String([1, 2, 3])).ToPin();
        var candidate = SshHostKeyVerifier.CreateCandidate(
            editedEndpoint,
            "ssh-ed25519",
            Convert.ToBase64String([1, 2, 3]));

        var result = SshHostKeyVerifier.Verify(candidate, pin);

        Assert.Equal(SshHostKeyStatus.Unknown, result.Status);
    }

    [Fact]
    public void Same_public_key_at_a_different_port_requires_confirmation()
    {
        var originalEndpoint = new SshHostKeyEndpoint("mac.example", 22);
        var editedEndpoint = new SshHostKeyEndpoint("mac.example", 2222);
        var pin = SshHostKeyVerifier.CreateCandidate(
            originalEndpoint,
            "ssh-ed25519",
            Convert.ToBase64String([1, 2, 3])).ToPin();
        var candidate = SshHostKeyVerifier.CreateCandidate(
            editedEndpoint,
            "ssh-ed25519",
            Convert.ToBase64String([1, 2, 3]));

        var result = SshHostKeyVerifier.Verify(candidate, pin);

        Assert.Equal(SshHostKeyStatus.Unknown, result.Status);
    }

    [Fact]
    public void Editing_profile_endpoint_preserves_but_does_not_rebind_the_old_pin()
    {
        var originalEndpoint = new SshHostKeyEndpoint("old.example", 22);
        var pin = SshHostKeyVerifier.CreateCandidate(
            originalEndpoint,
            "ssh-ed25519",
            Convert.ToBase64String([1, 2, 3])).ToPin();
        var profile = SshProfile.Create(
                "old.example",
                22,
                "operator",
                privateKeyPath: null,
                targetHost: "127.0.0.1",
                targetPort: 5900,
                credentialReference: null,
                pinnedHostKeyAlgorithm: null,
                pinnedHostKeySha256: null)
            .WithHostKeyPin(pin)
            .WithEndpoint("new.example", 2222);

        Assert.Equal(originalEndpoint, profile.HostKeyPin!.Endpoint);
        Assert.NotEqual(new SshHostKeyEndpoint(profile.Host, profile.Port), profile.HostKeyPin.Endpoint);
    }

    [Fact]
    public async Task Concurrent_confirmation_uses_insert_if_absent_CAS_and_never_overwrites()
    {
        var store = new InMemorySshHostKeyPinStore();
        var endpoint = new SshHostKeyEndpoint("mac.example", 22);
        var first = SshHostKeyVerifier.CreateCandidate(
            endpoint,
            "ssh-ed25519",
            Convert.ToBase64String([1, 2, 3])).ToPin();
        var second = SshHostKeyVerifier.CreateCandidate(
            endpoint,
            "ssh-ed25519",
            Convert.ToBase64String([3, 2, 1])).ToPin();

        var results = await Task.WhenAll(
            store.ConfirmUnknownAsync(first, CancellationToken.None).AsTask(),
            store.ConfirmUnknownAsync(second, CancellationToken.None).AsTask());
        var stored = await store.FindAsync(endpoint, CancellationToken.None);

        Assert.Single(results, result => result == SshHostKeyPinConfirmation.Stored);
        Assert.Single(results, result => result == SshHostKeyPinConfirmation.Conflict);
        Assert.True(stored == first || stored == second);
    }
}
