using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Transport.Tests;

public sealed class OpenSshExecutableResolverTests
{
    [Fact]
    public void Default_resolver_uses_verified_absolute_System32_paths_not_current_directory_names()
    {
        var systemDirectory = Path.GetFullPath(@"C:\Trusted Windows\System32");
        var expectedSsh = Path.Combine(
            systemDirectory,
            "OpenSSH",
            "ssh.exe");
        var expectedKeyScan = Path.Combine(
            systemDirectory,
            "OpenSSH",
            "ssh-keyscan.exe");
        var maliciousCurrentDirectorySsh = Path.GetFullPath("ssh.exe");
        var pollutedWindirSsh = Path.GetFullPath(
            @"C:\Attacker Controlled\System32\OpenSSH\ssh.exe");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            expectedSsh,
            expectedKeyScan,
            maliciousCurrentDirectorySsh,
            pollutedWindirSsh,
        };
        var resolver = new WindowsOpenSshExecutableResolver(
            new FakeSystemDirectoryProvider(systemDirectory),
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
            new FakeSystemDirectoryProvider(
                Path.GetFullPath(@"C:\Missing Windows\System32")),
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
            new FakeSystemDirectoryProvider(
                Path.GetFullPath(@"C:\Windows\System32")),
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

    [Fact]
    public void Complete_administrator_overrides_do_not_require_the_native_system_directory()
    {
        var sshPath = Path.GetFullPath(@"C:\Managed OpenSSH\ssh.exe");
        var keyScanPath = Path.GetFullPath(
            @"C:\Managed OpenSSH\ssh-keyscan.exe");
        var resolver = new WindowsOpenSshExecutableResolver(
            new ThrowingSystemDirectoryProvider(),
            sshPath,
            keyScanPath,
            _ => true);

        var resolved = resolver.Resolve();

        Assert.Equal(new OpenSshExecutablePaths(sshPath, keyScanPath), resolved);
    }

    [Fact]
    public void Native_provider_resizes_the_buffer_using_GetSystemDirectoryW_result()
    {
        var native = new FakeNativeSystemDirectoryApi
        {
            SystemDirectory = Path.GetFullPath(
                @"C:\A Windows Directory With A Long Name\System32"),
        };
        var provider = new WindowsSystemDirectoryProvider(
            native,
            initialBufferCapacity: 4,
            maximumBufferCapacity: 1024);

        var result = provider.GetSystemDirectory();

        Assert.Equal(native.SystemDirectory, result);
        Assert.Equal(2, native.CallCount);
        Assert.True(native.RequestedCapacities[1] > native.RequestedCapacities[0]);
    }

    [Fact]
    public void Native_provider_maps_GetSystemDirectoryW_failure_to_a_stable_exception()
    {
        var native = new FakeNativeSystemDirectoryApi
        {
            ReturnFailure = true,
            LastError = 5,
        };
        var provider = new WindowsSystemDirectoryProvider(
            native,
            initialBufferCapacity: 16,
            maximumBufferCapacity: 1024);

        var exception = Assert.Throws<OpenSshSystemDirectoryException>(
            provider.GetSystemDirectory);

        Assert.Equal(5, exception.NativeErrorCode);
    }

    [Fact]
    public void Native_provider_rejects_non_Windows_with_a_stable_exception()
    {
        var native = new FakeNativeSystemDirectoryApi
        {
            IsWindows = false,
        };
        var provider = new WindowsSystemDirectoryProvider(
            native,
            initialBufferCapacity: 16,
            maximumBufferCapacity: 1024);

        Assert.Throws<OpenSshPlatformNotSupportedException>(
            provider.GetSystemDirectory);
        Assert.Equal(0, native.CallCount);
    }

    private sealed class FakeSystemDirectoryProvider(string systemDirectory)
        : IWindowsSystemDirectoryProvider
    {
        public string GetSystemDirectory() => systemDirectory;
    }

    private sealed class ThrowingSystemDirectoryProvider
        : IWindowsSystemDirectoryProvider
    {
        public string GetSystemDirectory() =>
            throw new InvalidOperationException("native provider should not be called");
    }

    private sealed class FakeNativeSystemDirectoryApi
        : IWindowsSystemDirectoryNativeApi
    {
        public bool IsWindows { get; set; } = true;

        public string SystemDirectory { get; set; } =
            Path.GetFullPath(@"C:\Windows\System32");

        public bool ReturnFailure { get; set; }

        public int LastError { get; set; }

        public int CallCount { get; private set; }

        public List<uint> RequestedCapacities { get; } = [];

        public uint GetSystemDirectory(char[] buffer, uint capacity)
        {
            CallCount++;
            RequestedCapacities.Add(capacity);
            if (ReturnFailure)
            {
                return 0;
            }

            if (SystemDirectory.Length >= capacity)
            {
                return checked((uint)SystemDirectory.Length + 1);
            }

            SystemDirectory.AsSpan().CopyTo(buffer);
            return checked((uint)SystemDirectory.Length);
        }

        public int GetLastError() => LastError;
    }
}
