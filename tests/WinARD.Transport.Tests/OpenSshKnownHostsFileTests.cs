using System.Diagnostics;
using WinARD.Domain.Connections;
using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Transport.Tests;

public sealed class OpenSshKnownHostsFileTests
{
    [Fact]
    public async Task Factory_closes_the_creation_handle_before_returning_the_lease()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var cleanup = new TrackingFileCleanup();
        var permissions = new OpenReadPermissionSetter();
        var factory = new TemporaryOpenSshKnownHostsFileFactory(
            temporaryDirectory.Path,
            permissions,
            cleanup);

        var file = await factory.CreateAsync(Pin(), CancellationToken.None);
        await using (var readable = File.OpenRead(file.Path))
        {
            Assert.True(readable.Length > 0);
        }

        Assert.Equal(1, permissions.ApplyCount);
        Assert.True(permissions.CouldOpenForRead);
        await file.DisposeAsync();
        Assert.False(File.Exists(file.Path));
        Assert.Equal(1, cleanup.DeleteCount);
    }

    [Fact]
    public async Task Real_OpenSSH_tool_can_open_the_known_hosts_path_while_the_lease_is_alive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executables = new WindowsOpenSshExecutableResolver().Resolve();
        var sshKeygenPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(executables.SshPath)!,
            "ssh-keygen.exe");
        if (!File.Exists(sshKeygenPath))
        {
            return;
        }

        var factory = new TemporaryOpenSshKnownHostsFileFactory();
        await using var file = await factory.CreateAsync(Pin(), CancellationToken.None);
        var start = new ProcessStartInfo
        {
            FileName = sshKeygenPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-F");
        start.ArgumentList.Add("jump.example");
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add(file.Path);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Unable to start ssh-keygen.");

        await process.WaitForExitAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.DoesNotContain("Permission denied", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_failure_is_preserved_and_concurrent_dispose_runs_once()
    {
        var deleteFailure = new UnauthorizedAccessException("delete failed");
        var cleanup = new TrackingFileCleanup(deleteFailure);
        var file = new TemporaryOpenSshKnownHostsFile(
            @"C:\Temp\known_hosts",
            cleanup);

        var first = file.DisposeAsync().AsTask();
        var second = file.DisposeAsync().AsTask();

        Assert.Same(first, second);
        var firstFailure = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => first);
        var secondFailure = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => second);
        Assert.Same(deleteFailure, firstFailure);
        Assert.Same(deleteFailure, secondFailure);
        Assert.Equal(1, cleanup.DeleteCount);
    }

    [Fact]
    public async Task System_cleanup_still_attempts_delete_after_the_shared_deadline_expires()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(
            temporaryDirectory.Path,
            "expired-cleanup.known_hosts");
        await File.WriteAllTextAsync(path, "host key");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cleanup = new SystemOpenSshTemporaryFileCleanup();

        await cleanup.DeleteAsync(path, cancellation.Token);

        Assert.False(File.Exists(path));
    }

    private static SshHostKeyPin Pin() =>
        SshHostKeyVerifier.CreateCandidate(
            new SshHostKeyEndpoint("jump.example", 22),
            "ssh-ed25519",
            Convert.ToBase64String([1, 2, 3, 4])).ToPin();

    private sealed class OpenReadPermissionSetter
        : IOpenSshKnownHostsPermissions
    {
        public int ApplyCount { get; private set; }

        public bool CouldOpenForRead { get; private set; }

        public void Apply(string path)
        {
            ApplyCount++;
            using var stream = File.OpenRead(path);
            CouldOpenForRead = stream.CanRead;
        }
    }

    private sealed class TrackingFileCleanup(Exception? deleteException = null)
        : IOpenSshTemporaryFileCleanup
    {
        public int DeleteCount { get; private set; }

        public ValueTask DeleteAsync(
            string path,
            CancellationToken cancellationToken)
        {
            DeleteCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (deleteException is not null)
            {
                return ValueTask.FromException(deleteException);
            }

            File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"winard-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
