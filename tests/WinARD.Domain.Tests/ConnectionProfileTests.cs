using WinARD.Domain.Connections;
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
    public void New_profile_defaults_to_automatic_refresh()
    {
        var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user");

        Assert.Equal(QualityProfile.Automatic, profile.Quality);
        Assert.Equal(FrameRefreshPolicy.Automatic, profile.FrameRefreshPolicy);
    }

    [Fact]
    public void Updating_quality_replaces_entire_intent_and_preserves_connection_fields()
    {
        var original = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user")
            .WithCredential(CredentialReference.Create("windows", "mac-device-1"));
        var quality = QualityProfile.CreateCustom(
            null,
            QualityColor.Full32,
            QualityScale.Percent100,
            FrameRefreshPolicy.Fixed(60),
            allowAutomaticGrayscale: false,
            bandwidthLocked: true,
            colorLocked: true,
            scaleLocked: true,
            refreshLocked: true);

        var updated = original.WithQualityProfile(quality);

        Assert.Equal(quality, updated.Quality);
        Assert.Equal(quality.Refresh, updated.FrameRefreshPolicy);
        Assert.Equal(original.CredentialReference, updated.CredentialReference);
        Assert.Equal(original.Host, updated.Host);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(75)]
    [InlineData(90)]
    [InlineData(105)]
    [InlineData(120)]
    public void Fixed_refresh_accepts_only_supported_steps(int fps)
    {
        Assert.Equal(fps, FrameRefreshPolicy.Fixed(fps).FixedFramesPerSecond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(31)]
    [InlineData(121)]
    public void Fixed_refresh_rejects_unsupported_values(int fps) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameRefreshPolicy.Fixed(fps));

    [Fact]
    public void Supported_fixed_refresh_values_are_not_exposed_as_a_mutable_array()
    {
        var supportedValues = FrameRefreshPolicy.SupportedFixedFramesPerSecond;

        Assert.IsNotType<int[]>(supportedValues);
        var list = Assert.IsAssignableFrom<IList<int>>(supportedValues);
        Assert.Throws<NotSupportedException>(() => list[0] = 15);
    }

    [Fact]
    public void Refresh_policy_modes_expose_expected_fixed_value_semantics()
    {
        Assert.Equal(FrameRefreshMode.Automatic, FrameRefreshPolicy.Automatic.Mode);
        Assert.Null(FrameRefreshPolicy.Automatic.FixedFramesPerSecond);
        Assert.Equal(FrameRefreshMode.Unlimited, FrameRefreshPolicy.Unlimited.Mode);
        Assert.Null(FrameRefreshPolicy.Unlimited.FixedFramesPerSecond);

        var fixedPolicy = FrameRefreshPolicy.Fixed(60);

        Assert.Equal(FrameRefreshMode.Fixed, fixedPolicy.Mode);
        Assert.Equal(60, fixedPolicy.FixedFramesPerSecond);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(90)]
    public void Automatic_refresh_uses_remote_maximum(int? remoteMaximum)
    {
        Assert.Equal(remoteMaximum, FrameRefreshPolicy.Automatic.EffectiveMaximum(remoteMaximum));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(90)]
    public void Unlimited_refresh_has_no_effective_maximum(int? remoteMaximum)
    {
        Assert.Null(FrameRefreshPolicy.Unlimited.EffectiveMaximum(remoteMaximum));
    }

    [Fact]
    public void Fixed_refresh_without_remote_maximum_uses_fixed_value()
    {
        Assert.Equal(60, FrameRefreshPolicy.Fixed(60).EffectiveMaximum(null));
    }

    [Theory]
    [InlineData(45, 45)]
    [InlineData(90, 60)]
    public void Fixed_refresh_with_remote_maximum_uses_lower_value(int remoteMaximum, int expectedMaximum)
    {
        Assert.Equal(expectedMaximum, FrameRefreshPolicy.Fixed(60).EffectiveMaximum(remoteMaximum));
    }

    [Fact]
    public void Updating_refresh_preserves_connection_and_credentials()
    {
        var credential = CredentialReference.Create("windows", "mac-device-1");
        var original = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user")
            .WithCredential(credential);

        var updated = original.WithFrameRefreshPolicy(FrameRefreshPolicy.Unlimited);

        Assert.Equal(FrameRefreshPolicy.Unlimited, updated.FrameRefreshPolicy);
        Assert.Equal(FrameRefreshPolicy.Unlimited, updated.Quality.Refresh);
        Assert.Equal(original.Quality.WithRefresh(FrameRefreshPolicy.Unlimited), updated.Quality);
        Assert.Equal(original.CredentialReference, updated.CredentialReference);
        Assert.Equal(original.Host, updated.Host);
    }

    [Fact]
    public void Updating_credential_preserves_refresh_policy()
    {
        var original = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user")
            .WithFrameRefreshPolicy(FrameRefreshPolicy.Fixed(75));

        var updated = original.WithCredential(CredentialReference.Create("windows", "mac-device-1"));

        Assert.Equal(original.FrameRefreshPolicy, updated.FrameRefreshPolicy);
    }

    [Fact]
    public void Enabling_ssh_preserves_refresh_policy()
    {
        var original = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user")
            .WithFrameRefreshPolicy(FrameRefreshPolicy.Unlimited);
        var ssh = SshProfile.Create("bastion", 22, "jump", null, "mac", 5900, null, null, null);

        var updated = original.WithSsh(ssh);

        Assert.Equal(original.FrameRefreshPolicy, updated.FrameRefreshPolicy);
    }

    [Fact]
    public void Disabling_ssh_preserves_refresh_policy()
    {
        var original = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user")
            .WithSsh(SshProfile.Create("bastion", 22, "jump", null, "mac", 5900, null, null, null))
            .WithFrameRefreshPolicy(FrameRefreshPolicy.Fixed(105));

        var updated = original.WithoutSsh();

        Assert.Equal(original.FrameRefreshPolicy, updated.FrameRefreshPolicy);
    }

    [Fact]
    public void Other_with_methods_preserve_complete_quality_intent()
    {
        var quality = QualityProfile.CreateCustom(
            8L * 1024 * 1024,
            QualityColor.Grayscale,
            QualityScale.Percent75,
            FrameRefreshPolicy.Fixed(105),
            allowAutomaticGrayscale: true,
            bandwidthLocked: true,
            colorLocked: false,
            scaleLocked: true,
            refreshLocked: true);
        var original = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user")
            .WithQualityProfile(quality);
        var ssh = SshProfile.Create("bastion", 22, "jump", null, "mac", 5900, null, null, null);

        Assert.Equal(quality, original.WithCredential(CredentialReference.Create("windows", "key")).Quality);
        Assert.Equal(quality, original.WithSsh(ssh).Quality);
        Assert.Equal(quality, original.WithSsh(ssh).WithoutSsh().Quality);
    }

}
