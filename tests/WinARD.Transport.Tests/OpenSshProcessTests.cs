using System.Text;
using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Transport.Tests;

public sealed class OpenSshProcessTests
{
    [Fact]
    public async Task System_launcher_rejects_relative_executables_before_creating_a_process()
    {
        var launcher = new SystemOpenSshProcessLauncher();

        await Assert.ThrowsAsync<OpenSshExecutableConfigurationException>(
            () => launcher
                .LaunchAsync(
                    new OpenSshProcessStart("malicious-ssh.exe", []),
                    CancellationToken.None)
                .AsTask());
    }

    [Fact]
    public async Task Oversized_keyscan_stdout_kills_waits_and_disposes_the_process()
    {
        var process = new FakeProcess
        {
            StandardOutputSource = new MemoryStream(new byte[33]),
        };
        var launcher = CreateLauncher(process, maximumBytes: 32);

        var exception = await Assert.ThrowsAsync<OpenSshOutputLimitExceededException>(
            () => launcher.ScanAsync(Start(), CancellationToken.None).AsTask());

        Assert.Equal(OpenSshOutputKind.StandardOutput, exception.OutputKind);
        Assert.Equal(32, exception.MaximumBytes);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(2, process.WaitCount);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task Infinite_keyscan_output_is_stopped_at_the_configured_byte_limit()
    {
        var process = new FakeProcess
        {
            StandardOutputSource = new InfiniteReadStream(),
        };
        var launcher = CreateLauncher(process, maximumBytes: 64);

        await Assert.ThrowsAsync<OpenSshOutputLimitExceededException>(
            () => launcher.ScanAsync(Start(), CancellationToken.None).AsTask());

        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task Keyscan_failure_observes_canceled_reader_tasks_before_returning()
    {
        var delayedReader = new DelayedCancellationFailureStream(
            TimeSpan.FromMilliseconds(50));
        var process = new FakeProcess
        {
            StandardOutputSource = new MemoryStream(new byte[33]),
            StandardErrorSource = delayedReader,
        };
        var launcher = CreateLauncher(process, maximumBytes: 32);

        await Assert.ThrowsAsync<OpenSshOutputLimitExceededException>(
            () => launcher.ScanAsync(Start(), CancellationToken.None).AsTask());

        Assert.True(delayedReader.Finished.IsCompleted);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task Caller_cancellation_preserves_the_primary_exception_and_all_cleanup_failures()
    {
        var process = new FakeProcess
        {
            StandardOutputSource = new BlockingReadStream(),
            StandardErrorSource = new BlockingReadStream(),
            KillException = new InvalidOperationException("kill failed"),
            CleanupWaitException = new IOException("wait failed"),
            DisposeException = new IOException("dispose failed"),
        };
        var launcher = CreateLauncher(process, maximumBytes: 64);
        using var cancellation = new CancellationTokenSource();

        var scanTask = launcher.ScanAsync(Start(), cancellation.Token).AsTask();
        await process.WaitStarted.Task;
        cancellation.Cancel();
        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => scanTask);

        Assert.IsAssignableFrom<OperationCanceledException>(aggregate.InnerExceptions[0]);
        Assert.Equal("kill failed", aggregate.InnerExceptions[1].Message);
        Assert.Equal("wait failed", aggregate.InnerExceptions[2].Message);
        Assert.Equal("dispose failed", aggregate.InnerExceptions[3].Message);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(2, process.WaitCount);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task Cleanup_wait_uses_an_independent_short_timeout_and_still_disposes()
    {
        var process = new FakeProcess
        {
            StandardOutputSource = new MemoryStream(new byte[33]),
            CleanupWaitBlocks = true,
        };
        var launcher = new SystemOpenSshKeyScanLauncher(
            new FakeLauncher(process),
            new OpenSshKeyScanLimits(
                maximumStandardOutputBytes: 32,
                maximumStandardErrorBytes: 32,
                cleanupTimeout: TimeSpan.FromMilliseconds(20)),
            TimeProvider.System);

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => launcher.ScanAsync(Start(), CancellationToken.None).AsTask());

        Assert.IsType<OpenSshOutputLimitExceededException>(aggregate.InnerExceptions[0]);
        Assert.Contains(
            aggregate.InnerExceptions,
            exception => exception is OpenSshProcessCleanupTimeoutException);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task Nonzero_keyscan_exit_is_rejected_with_bounded_redacted_stderr()
    {
        var process = new FakeProcess
        {
            StandardErrorSource = new MemoryStream(
                Encoding.UTF8.GetBytes(
                    "password=topsecret " + new string('x', 32))),
        };
        process.Exit(1);
        var launcher = CreateLauncher(process, maximumBytes: 64);

        var exception = await Assert.ThrowsAsync<OpenSshKeyScanProcessException>(
            () => launcher.ScanAsync(Start(), CancellationToken.None).AsTask());

        Assert.Equal(1, exception.ExitCode);
        Assert.DoesNotContain(
            "topsecret",
            exception.DiagnosticSummary,
            StringComparison.Ordinal);
        Assert.True(exception.DiagnosticSummary.Length <= 64);
        Assert.Equal(0, process.KillCount);
        Assert.Equal(2, process.WaitCount);
        Assert.Equal(1, process.DisposeCount);
    }

    private static SystemOpenSshKeyScanLauncher CreateLauncher(
        FakeProcess process,
        int maximumBytes) =>
        new(
            new FakeLauncher(process),
            new OpenSshKeyScanLimits(
                maximumBytes,
                maximumBytes,
                TimeSpan.FromSeconds(1)),
            TimeProvider.System);

    private static OpenSshProcessStart Start() =>
        new(
            Path.GetFullPath(@"C:\Windows\System32\OpenSSH\ssh-keyscan.exe"),
            []);

    private sealed class FakeLauncher(FakeProcess process)
        : IOpenSshProcessLauncher
    {
        public ValueTask<IOpenSshProcess> LaunchAsync(
            OpenSshProcessStart start,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IOpenSshProcess>(process);
        }
    }

    private sealed class FakeProcess : IOpenSshProcess
    {
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Stream StandardInput { get; } = new MemoryStream();

        public Stream StandardOutput => StandardOutputSource;

        public Stream StandardError => StandardErrorSource;

        public Stream StandardOutputSource { get; set; } = Stream.Null;

        public Stream StandardErrorSource { get; set; } = Stream.Null;

        public Exception? KillException { get; set; }

        public Exception? CleanupWaitException { get; set; }

        public Exception? DisposeException { get; set; }

        public bool CleanupWaitBlocks { get; set; }

        public TaskCompletionSource WaitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited => _exit.Task.IsCompleted;

        public int? ExitCode => HasExited ? _exit.Task.Result : null;

        public int KillCount { get; private set; }

        public int WaitCount { get; private set; }

        public int DisposeCount { get; private set; }

        public void Exit(int exitCode) => _exit.TrySetResult(exitCode);

        public void Kill(bool entireProcessTree)
        {
            KillCount++;
            if (KillException is not null)
            {
                throw KillException;
            }

            _exit.TrySetResult(-1);
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCount++;
            WaitStarted.TrySetResult();
            if (WaitCount > 1 && CleanupWaitException is not null)
            {
                throw CleanupWaitException;
            }

            if (WaitCount > 1 && CleanupWaitBlocks)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            await _exit.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            StandardInput.Dispose();
            StandardOutputSource.Dispose();
            StandardErrorSource.Dispose();
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }
    }

    private sealed class InfiniteReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Span.Fill(1);
            return ValueTask.FromResult(buffer.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DelayedCancellationFailureStream(TimeSpan delay) : Stream
    {
        private readonly TaskCompletionSource _finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Finished => _finished.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(delay, CancellationToken.None);
                _finished.TrySetResult();
                throw new IOException("reader cleanup failed");
            }

            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
