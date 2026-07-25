using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Transport.Tests;

public sealed class OpenSshExecutableResolverTests
{
    [Fact]
    public void Default_resolver_uses_verified_absolute_System32_paths_not_current_directory_names()
    {
        var windowsDirectory = Path.GetFullPath(@"C:\Trusted Windows");
        var expectedSsh = Path.Combine(
            windowsDirectory,
            "System32",
            "OpenSSH",
            "ssh.exe");
        var expectedKeyScan = Path.Combine(
            windowsDirectory,
            "System32",
            "OpenSSH",
            "ssh-keyscan.exe");
        var maliciousCurrentDirectorySsh = Path.GetFullPath("ssh.exe");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            expectedSsh,
            expectedKeyScan,
            maliciousCurrentDirectorySsh,
        };
        var resolver = new WindowsOpenSshExecutableResolver(
            windowsDirectory,
            configuredSshPath: null,
            configuredKeyScanPath: null,
            existing.Contains);

        var resolved = resolver.Resolve();

        Assert.Equal(expectedSsh, resolved.SshPath);
        Assert.Equal(expectedKeyScan, resolved.KeyScanPath);
        Assert.NotEqual(maliciousCurrentDirectorySsh, resolved.SshPath);
        Assert.True(Path.IsPathFullyQualified(resolved.SshPath));
        Assert.True(Path.IsPathFullyQualified(resolved.KeyScanPath));
    }

    [Fact]
    public void Missing_system_executable_throws_a_stable_resolution_exception()
    {
        var resolver = new WindowsOpenSshExecutableResolver(
            Path.GetFullPath(@"C:\Missing Windows"),
            configuredSshPath: null,
            configuredKeyScanPath: null,
            _ => false);

        var exception = Assert.Throws<OpenSshExecutableNotFoundException>(
            resolver.Resolve);

        Assert.Equal(OpenSshExecutableKind.Ssh, exception.ExecutableKind);
        Assert.True(Path.IsPathFullyQualified(exception.ExpectedPath));
    }

    [Theory]
    [InlineData("ssh.exe", null)]
    [InlineData(null, "ssh-keyscan.exe")]
    [InlineData(@"C:\Tools\not-ssh.exe", null)]
    [InlineData(null, @"C:\Tools\not-keyscan.exe")]
    public void Explicit_administrator_paths_must_be_absolute_and_have_the_expected_file_name(
        string? configuredSshPath,
        string? configuredKeyScanPath)
    {
        var resolver = new WindowsOpenSshExecutableResolver(
            Path.GetFullPath(@"C:\Windows"),
            configuredSshPath,
            configuredKeyScanPath,
            _ => true);

        Assert.Throws<OpenSshExecutableConfigurationException>(resolver.Resolve);
    }

    [Fact]
    public void Absolute_executable_paths_with_spaces_remain_the_ProcessStartInfo_file_name()
    {
        var paths = new OpenSshExecutablePaths(
            Path.GetFullPath(@"C:\Program Files\OpenSSH\ssh.exe"),
            Path.GetFullPath(@"C:\Program Files\OpenSSH\ssh-keyscan.exe"));

        var start = OpenSshCommandBuilder.BuildKeyScan(
            paths.KeyScanPath,
            new("jump.example", 2222),
            TimeSpan.FromSeconds(7));

        Assert.Equal(paths.KeyScanPath, start.FileName);
        Assert.Equal(["-T", "7", "-p", "2222", "jump.example"], start.Arguments);
    }
}
