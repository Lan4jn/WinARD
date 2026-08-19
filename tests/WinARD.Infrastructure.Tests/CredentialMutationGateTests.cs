using WinARD.Infrastructure.Settings;
using Xunit;
using System.Diagnostics;

#pragma warning disable CA1707

namespace WinARD.Infrastructure.Tests;

public sealed class CredentialMutationGateTests
{
    private static readonly string[] PowerShellExecutables = ["pwsh.exe", "powershell.exe"];

    [Fact]
    public async Task Same_database_path_coordinates_across_gate_instances()
    {
        var path = Path.Combine(Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"), "winard.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var first = new CredentialMutationGate(path);
        using var second = new CredentialMutationGate(path.ToUpperInvariant());
        using var held = await first.EnterAsync(CancellationToken.None);

        var waiting = second.EnterAsync(CancellationToken.None).AsTask();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            waiting.WaitAsync(TimeSpan.FromMilliseconds(150)));

        held.Dispose();
        using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Different_database_paths_do_not_block_each_other()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var first = new CredentialMutationGate(Path.Combine(directory, "first.db"));
        using var second = new CredentialMutationGate(Path.Combine(directory, "second.db"));
        using var held = await first.EnterAsync(CancellationToken.None);

        using var acquired = await second.EnterAsync(CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Cancelled_wait_does_not_consume_or_leak_the_gate()
    {
        var path = Path.Combine(Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"), "winard.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var first = new CredentialMutationGate(path);
        using var second = new CredentialMutationGate(path);
        using var held = await first.EnterAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = second.EnterAsync(cancellation.Token).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();

        using var acquired = await second.EnterAsync(CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Dispose_cancels_wait_without_releasing_another_lease()
    {
        var path = Path.Combine(Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"), "winard.db");
        using var holder = new CredentialMutationGate(path);
        var waitingGate = new CredentialMutationGate(path);
        using var held = await holder.EnterAsync(CancellationToken.None);
        var waiting = waitingGate.EnterAsync(CancellationToken.None).AsTask();

        waitingGate.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
        using var observer = new CredentialMutationGate(path);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            observer.EnterAsync(cancellation.Token).AsTask());

        held.Dispose();
        using var acquired = await observer.EnterAsync(CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Dispose_after_file_open_prevents_lease_return()
    {
        var path = Path.Combine(Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"), "winard.db");
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new CredentialMutationGate(path, async cancellationToken =>
        {
            opened.SetResult();
            await resume.Task.WaitAsync(cancellationToken);
        });
        var acquire = gate.EnterAsync(CancellationToken.None).AsTask();
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(2));

        gate.Dispose();
        resume.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => acquire);
        using var observer = new CredentialMutationGate(path);
        using var acquired = await observer.EnterAsync(CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Non_lock_io_failure_is_not_retried()
    {
        var path = Path.Combine(Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"), "winard.db");
        var attempts = 0;
        using var gate = new CredentialMutationGate(path, _ =>
        {
            attempts++;
            throw new IOException("Disk full.", unchecked((int)0x80070070));
        });

        var error = await Assert.ThrowsAsync<IOException>(() =>
            gate.EnterAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(unchecked((int)0x80070070), error.HResult);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Killed_helper_process_releases_cross_process_lock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "winard.db");
        var readyPath = Path.Combine(directory, "ready");
        using var gate = new CredentialMutationGate(databasePath);
        using var helper = StartLockHolder(gate.LockFilePath, readyPath);
        try
        {
            await WaitForReadyAsync(helper, readyPath);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                gate.EnterAsync(cancellation.Token).AsTask());

            helper.Kill(entireProcessTree: true);
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var acquired = await gate.EnterAsync(CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill(entireProcessTree: true);
            }
        }
    }

    private static Process StartLockHolder(string lockPath, string readyPath)
    {
        var start = new ProcessStartInfo(
            FindPowerShell(),
            "-NoLogo -NoProfile -NonInteractive -Command \"$s=[IO.File]::Open($env:WINARD_LOCK_PATH,'OpenOrCreate','ReadWrite','None');[IO.File]::WriteAllText($env:WINARD_READY_PATH,'ready');[Threading.Thread]::Sleep([int]::MaxValue)\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(lockPath)!,
        };
        start.Environment["WINARD_LOCK_PATH"] = lockPath;
        start.Environment["WINARD_READY_PATH"] = readyPath;
        return Process.Start(start) ?? throw new InvalidOperationException("Lock holder failed to start.");
    }

    private static string FindPowerShell()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (from name in PowerShellExecutables
                from directory in directories
                let path = Path.Combine(directory.Trim('"'), name)
                where File.Exists(path)
                select path).FirstOrDefault()
            ?? throw new InvalidOperationException("PowerShell was not found on PATH.");
    }

    private static async Task WaitForReadyAsync(Process helper, string readyPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(readyPath))
        {
            if (helper.HasExited)
            {
                throw new InvalidOperationException($"Lock holder exited with {helper.ExitCode}.");
            }
            await Task.Delay(20, timeout.Token);
        }
    }
}
