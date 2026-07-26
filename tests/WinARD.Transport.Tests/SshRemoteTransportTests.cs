using System.Diagnostics;
using System.Net;
using System.Text;
using WinARD.Domain.Connections;
using WinARD.Domain.Security;
using WinARD.Transport;
using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Transport.Tests;

public sealed class SshRemoteTransportTests
{
    [Fact]
    public async Task Unknown_keyscan_result_requires_confirmation_without_launching_ssh()
    {
        var fixture = new OpenSshFixture();

        var exception = await Assert.ThrowsAsync<SshHostKeyUnknownException>(
            () => fixture.Transport.ConnectAsync(fixture.Profile, CancellationToken.None));

        Assert.Equal(SshHostKeyStatus.Unknown, exception.Verification.Status);
        Assert.Equal(0, fixture.Launcher.LaunchCount);
        Assert.Equal(0, fixture.KnownHosts.CreateCount);
    }

    [Fact]
    public async Task Changed_key_is_blocked_without_overwriting_pin_or_launching_ssh()
    {
        var fixture = new OpenSshFixture();
        var originalPin = fixture.Candidate.ToPin();
        await fixture.Store.ConfirmUnknownAsync(originalPin, CancellationToken.None);
        fixture.KeyScan.Output = fixture.KeyScanLine([9, 9, 9]);

        await Assert.ThrowsAsync<SshHostKeyChangedException>(
            () => fixture.Transport.ConnectAsync(fixture.Profile, CancellationToken.None));

        Assert.Equal(originalPin, await fixture.Store.FindAsync(fixture.Endpoint, CancellationToken.None));
        Assert.Equal(0, fixture.Launcher.LaunchCount);
    }

    [Fact]
    public async Task Trusted_key_launches_strict_stdio_forward_and_preserves_ready_byte()
    {
        var fixture = new OpenSshFixture();
        await fixture.ConfirmAsync();
        fixture.Process.StandardOutputSource = new MemoryStream("RFB 003.889\n"u8.ToArray());

        await using var connection = await fixture.Transport.ConnectAsync(
            fixture.Profile,
            CancellationToken.None);
        var banner = new byte[12];
        await connection.Stream.ReadExactlyAsync(banner);

        Assert.Equal("RFB 003.889\n", Encoding.ASCII.GetString(banner));
        Assert.Contains("-W", fixture.Launcher.LastStart!.Arguments);
        Assert.DoesNotContain(
            typeof(SshRemoteTransport).Assembly.GetReferencedAssemblies(),
            assembly => string.Equals(assembly.Name, "Renci.SshNet", StringComparison.Ordinal));
        Assert.Equal(OpenSshKnownHosts.Format(fixture.Candidate.ToPin()), fixture.KnownHosts.Content);
    }

    [Fact]
    public async Task Remote_tunnel_rejection_fails_before_returning_a_stream_and_cleans_resources()
    {
        var fixture = new OpenSshFixture();
        await fixture.ConfirmAsync();
        fixture.Process.StandardOutputSource = Stream.Null;
        fixture.Process.StandardErrorSource = new MemoryStream(
            "channel 0: open failed: connect failed: Connection refused"u8.ToArray());
        fixture.Process.Exit(255);

        var exception = await Assert.ThrowsAsync<OpenSshTunnelException>(
            () => fixture.Transport.ConnectAsync(fixture.Profile, CancellationToken.None));

        Assert.Contains("Connection refused", exception.DiagnosticSummary, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Process.DisposeCount);
        Assert.Equal(1, fixture.KnownHosts.DisposeCount);
    }

    [Fact]
    public async Task Total_deadline_covers_keyscan_and_remote_ready_probe()
    {
        var timeProvider = new ManualTimeProvider();
        var fixture = new OpenSshFixture(
            timeProvider,
            new TransportTimeouts(TimeSpan.FromSeconds(30)));
        await fixture.ConfirmAsync();
        fixture.KeyScan.OnScan = () => timeProvider.Advance(TimeSpan.FromSeconds(25));
        fixture.Process.StandardOutputSource = new BlockingReadStream();

        var connectTask = fixture.Transport.ConnectAsync(fixture.Profile, CancellationToken.None);
        await fixture.Process.ReadStarted.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<TransportTimeoutException>(() => connectTask);
        Assert.Equal(TransportTimeoutStage.Connection, exception.Stage);
        Assert.Equal(1, fixture.Process.KillCount);
        Assert.Equal(1, fixture.KnownHosts.DisposeCount);
        Assert.Equal(0, timeProvider.ActiveTimerCount);
    }

    [Fact]
    public async Task Caller_cancellation_kills_process_and_is_not_mapped_to_timeout()
    {
        var fixture = new OpenSshFixture();
        await fixture.ConfirmAsync();
        fixture.Process.StandardOutputSource = new BlockingReadStream();
        using var cancellation = new CancellationTokenSource();

        var connectTask = fixture.Transport.ConnectAsync(fixture.Profile, cancellation.Token);
        await fixture.Process.ReadStarted.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask);
        Assert.IsNotType<TransportTimeoutException>(exception);
        Assert.Equal(1, fixture.Process.KillCount);
        Assert.Equal(1, fixture.KnownHosts.DisposeCount);
    }

    [Fact]
    public async Task Large_stderr_is_drained_without_deadlock_and_sensitive_assignments_are_redacted()
    {
        var fixture = new OpenSshFixture();
        await fixture.ConfirmAsync();
        fixture.Process.StandardOutputSource = Stream.Null;
        var stderr = string.Concat(
            Enumerable.Repeat("password=topsecret " + new string('x', 1024), 128));
        fixture.Process.StandardErrorSource = new MemoryStream(Encoding.UTF8.GetBytes(stderr));
        fixture.Process.Exit(255);

        var exception = await Assert.ThrowsAsync<OpenSshTunnelException>(
            () => fixture.Transport.ConnectAsync(fixture.Profile, CancellationToken.None));

        Assert.DoesNotContain("topsecret", exception.DiagnosticSummary, StringComparison.Ordinal);
        Assert.True(exception.DiagnosticSummary.Length <= OpenSshDiagnostics.MaximumSummaryLength);
    }

    [Fact]
    public async Task Tunnel_exception_message_is_stable_and_diagnostics_redact_OpenSSH_paths()
    {
        var fixture = new OpenSshFixture();
        await fixture.ConfirmAsync();
        fixture.Process.StandardOutputSource = Stream.Null;
        var identityPath = @"C:\Users\Alice\.ssh\id_ed25519";
        var knownHostsPath =
            @"C:\Users\Alice\AppData\Local\Temp\winard-0123456789abcdef0123456789abcdef.known_hosts";
        fixture.Process.StandardErrorSource = new MemoryStream(
            Encoding.UTF8.GetBytes(
                $"identity file \"{identityPath}\" type 3; " +
                $"UserKnownHostsFile=\"{knownHostsPath}\"; Connection refused"));
        fixture.Process.Exit(255);

        var exception = await Assert.ThrowsAsync<OpenSshTunnelException>(
            () => fixture.Transport.ConnectAsync(fixture.Profile, CancellationToken.None));

        Assert.DoesNotContain("Connection refused", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(identityPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(knownHostsPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection refused", exception.DiagnosticSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(
            identityPath,
            exception.DiagnosticSummary,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            knownHostsPath,
            exception.DiagnosticSummary,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrent_stream_disposal_kills_and_cleans_process_once()
    {
        var fixture = new OpenSshFixture();
        await fixture.ConfirmAsync();
        fixture.Process.StandardOutputSource = new MemoryStream([42]);
        var connection = await fixture.Transport.ConnectAsync(
            fixture.Profile,
            CancellationToken.None);

        await Task.WhenAll(
            connection.DisposeAsync().AsTask(),
            connection.DisposeAsync().AsTask());

        Assert.Equal(1, fixture.Process.KillCount);
        Assert.Equal(1, fixture.Process.DisposeCount);
        Assert.Equal(1, fixture.KnownHosts.DisposeCount);
    }

    [Fact]
    public async Task Tunnel_cleanup_uses_one_short_deadline_and_attempts_every_step()
    {
        var process = new FakeOpenSshProcess
        {
            KillException = new InvalidOperationException("kill failed"),
            WaitBlocks = true,
            DisposeException = new IOException("process dispose failed"),
        };
        var knownHosts = new FakeKnownHostsFile(
            new UnauthorizedAccessException("known_hosts delete failed"));
        var diagnostics = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new OpenSshTunnelLifetime(
            process,
            knownHosts,
            diagnostics.Task,
            new OpenSshTunnelCleanupOptions(TimeSpan.FromMilliseconds(30)),
            TimeProvider.System);
        var stopwatch = Stopwatch.StartNew();

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => lifetime.DisposeAsync().AsTask());

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Contains(
            aggregate.InnerExceptions,
            exception => exception.Message == "kill failed");
        Assert.Contains(
            aggregate.InnerExceptions,
            exception => exception is OpenSshTunnelCleanupTimeoutException);
        Assert.Contains(
            aggregate.InnerExceptions,
            exception => exception.Message == "process dispose failed");
        Assert.Contains(
            aggregate.InnerExceptions,
            exception => exception.Message == "known_hosts delete failed");
        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.WaitCount);
        Assert.Equal(1, process.DisposeCount);
        Assert.Equal(1, knownHosts.DeleteCount);
    }

    [Fact]
    public async Task Tunnel_cleanup_ignores_the_has_exited_kill_race()
    {
        var process = new FakeOpenSshProcess
        {
            ExitBeforeKillException = true,
            KillException = new InvalidOperationException("already exited"),
        };
        var knownHosts = new FakeKnownHostsFile();
        var lifetime = new OpenSshTunnelLifetime(
            process,
            knownHosts,
            Task.FromResult(string.Empty),
            OpenSshTunnelCleanupOptions.Default,
            TimeProvider.System);

        await lifetime.DisposeAsync();

        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.WaitCount);
        Assert.Equal(1, process.DisposeCount);
        Assert.Equal(1, knownHosts.DeleteCount);
    }

    [Fact]
    public async Task Tunnel_cleanup_bounds_a_blocking_synchronous_kill()
    {
        using var killGate = new ManualResetEventSlim();
        var process = new FakeOpenSshProcess
        {
            KillGate = killGate,
            WaitBlocks = true,
        };
        var knownHosts = new FakeKnownHostsFile();
        var diagnostics = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new OpenSshTunnelLifetime(
            process,
            knownHosts,
            diagnostics.Task,
            new OpenSshTunnelCleanupOptions(TimeSpan.FromMilliseconds(100)),
            TimeProvider.System);
        var stopwatch = Stopwatch.StartNew();

        OpenSshTunnelCleanupTimeoutException timeout;
        try
        {
            var disposeTask = lifetime.DisposeAsync().AsTask();
            await process.KillStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            timeout = await Assert.ThrowsAsync<OpenSshTunnelCleanupTimeoutException>(
                () => disposeTask);
        }
        finally
        {
            stopwatch.Stop();
            killGate.Set();
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromMilliseconds(100), timeout.Timeout);
        Assert.Equal(1, process.WaitCount);
        Assert.Equal(1, process.DisposeCount);
        Assert.Equal(1, knownHosts.DeleteCount);
    }

    [Fact]
    public async Task Credential_reference_is_rejected_by_unsupported_askpass_before_process_launch()
    {
        var fixture = new OpenSshFixture();
        await fixture.ConfirmAsync();
        var current = fixture.Profile.SshProfile!;
        var ssh = SshProfile.Create(
            current.Host,
            current.Port,
            current.Username,
            current.PrivateKeyPath,
            current.TargetHost,
            current.TargetPort,
            CredentialReference.Create("windows", "ssh-secret"),
            pinnedHostKeyAlgorithm: null,
            pinnedHostKeySha256: null);
        var profile = ConnectionProfile
            .Create(Guid.NewGuid(), "Mac", "ignored.example", 5999, "operator")
            .WithSsh(ssh);

        await Assert.ThrowsAsync<OpenSshAuthenticationUnsupportedException>(
            () => fixture.Transport.ConnectAsync(profile, CancellationToken.None));

        Assert.Equal(0, fixture.Launcher.LaunchCount);
    }

    [Fact]
    public async Task Askpass_session_configures_launch_and_is_disposed_with_tunnel()
    {
        var askPass = new RecordingAskPassBroker();
        var fixture = new OpenSshFixture(askPassBroker: askPass);
        await fixture.ConfirmAsync();
        var current = fixture.Profile.SshProfile!;
        var ssh = SshProfile.Create(
            current.Host,
            current.Port,
            current.Username,
            current.PrivateKeyPath,
            current.TargetHost,
            current.TargetPort,
            CredentialReference.Create("windows", "ssh-secret"),
            pinnedHostKeyAlgorithm: null,
            pinnedHostKeySha256: null);
        var profile = ConnectionProfile
            .Create(Guid.NewGuid(), "Mac", "ignored.example", 5999, "operator")
            .WithSsh(ssh);

        await using var connection = await fixture.Transport.ConnectAsync(
            profile,
            CancellationToken.None);

        Assert.Equal(1, askPass.PrepareCount);
        Assert.Equal("configured", fixture.Launcher.LastStart!.Environment!["ASKPASS_TEST"]);
        Assert.Equal(0, askPass.Session.DisposeCount);
        await connection.DisposeAsync();
        Assert.Equal(1, askPass.Session.DisposeCount);
    }

    [Fact]
    public async Task Launch_failure_disposes_prepared_askpass_session()
    {
        var askPass = new RecordingAskPassBroker();
        var fixture = new OpenSshFixture(askPassBroker: askPass);
        await fixture.ConfirmAsync();
        fixture.Launcher.LaunchException = new IOException("launch failed");

        await Assert.ThrowsAsync<IOException>(
            () => fixture.Transport.ConnectAsync(
                fixture.Profile,
                CancellationToken.None));

        Assert.Equal(1, askPass.Session.DisposeCount);
    }

    [Fact]
    public async Task Launch_failure_preserves_primary_and_all_known_hosts_cleanup_failures()
    {
        var fixture = new OpenSshFixture();
        await fixture.ConfirmAsync();
        var primary = new IOException("launch failed");
        var streamCleanup = new IOException("stream dispose failed");
        var deleteCleanup = new UnauthorizedAccessException("delete failed");
        fixture.Launcher.LaunchException = primary;
        fixture.KnownHosts.DisposeException =
            new AggregateException(streamCleanup, deleteCleanup);

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => fixture.Transport.ConnectAsync(
                fixture.Profile,
                CancellationToken.None));

        Assert.Equal(3, aggregate.InnerExceptions.Count);
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Same(streamCleanup, aggregate.InnerExceptions[1]);
        Assert.Same(deleteCleanup, aggregate.InnerExceptions[2]);
    }

    private sealed class OpenSshFixture
    {
        public OpenSshFixture(
            TimeProvider? timeProvider = null,
            TransportTimeouts? timeouts = null,
            IOpenSshAskPassBroker? askPassBroker = null)
        {
            Endpoint = new SshHostKeyEndpoint("jump.example", 2222);
            Candidate = SshHostKeyVerifier.CreateCandidate(
                Endpoint,
                "ssh-ed25519",
                Convert.ToBase64String([1, 2, 3, 4]));
            KeyScan = new FakeKeyScanLauncher
            {
                Output = KeyScanLine([1, 2, 3, 4]),
            };
            Process = new FakeOpenSshProcess();
            Launcher = new FakeProcessLauncher(Process);
            Store = new InMemorySshHostKeyPinStore();
            KnownHosts = new FakeKnownHostsFactory();
            var ssh = SshProfile.Create(
                Endpoint.Host,
                Endpoint.Port,
                "ssh-user",
                privateKeyPath: null,
                targetHost: "127.0.0.1",
                targetPort: 5900,
                credentialReference: null,
                pinnedHostKeyAlgorithm: null,
                pinnedHostKeySha256: null);
            Profile = ConnectionProfile
                .Create(Guid.NewGuid(), "Mac", "ignored.example", 5999, "operator")
                .WithSsh(ssh);
            Transport = new SshRemoteTransport(
                Launcher,
                KeyScan,
                Store,
                askPassBroker ?? new UnsupportedOpenSshAskPassBroker(),
                KnownHosts,
                new FakeExecutableResolver(),
                timeouts ?? TransportTimeouts.Default,
                timeProvider ?? TimeProvider.System);
        }

        public SshHostKeyEndpoint Endpoint { get; }

        public SshHostKeyCandidate Candidate { get; }

        public FakeKeyScanLauncher KeyScan { get; }

        public FakeOpenSshProcess Process { get; }

        public FakeProcessLauncher Launcher { get; }

        public InMemorySshHostKeyPinStore Store { get; }

        public FakeKnownHostsFactory KnownHosts { get; }

        public ConnectionProfile Profile { get; }

        public SshRemoteTransport Transport { get; }

        public Task<SshHostKeyPinConfirmation> ConfirmAsync() =>
            Store.ConfirmUnknownAsync(Candidate.ToPin(), CancellationToken.None).AsTask();

        public string KeyScanLine(byte[] key) =>
            $"[{Endpoint.Host}]:{Endpoint.Port} ssh-ed25519 {Convert.ToBase64String(key)}";
    }

    private sealed class RecordingAskPassBroker : IOpenSshAskPassBroker
    {
        public int PrepareCount { get; private set; }

        public RecordingAskPassSession Session { get; } = new();

        public ValueTask EnsureSupportedAsync(
            SshProfile profile,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<IOpenSshAskPassSession> PrepareAsync(
            SshProfile profile,
            CancellationToken cancellationToken)
        {
            PrepareCount++;
            return ValueTask.FromResult<IOpenSshAskPassSession>(Session);
        }
    }

    private sealed class RecordingAskPassSession : IOpenSshAskPassSession
    {
        public int DisposeCount { get; private set; }

        public OpenSshProcessStart Configure(OpenSshProcessStart start) =>
            start with
            {
                Environment = new Dictionary<string, string>
                {
                    ["ASKPASS_TEST"] = "configured",
                },
            };

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeExecutableResolver : IOpenSshExecutableResolver
    {
        public OpenSshExecutablePaths Resolve() =>
            new(
                Path.GetFullPath(@"C:\Windows\System32\OpenSSH\ssh.exe"),
                Path.GetFullPath(@"C:\Windows\System32\OpenSSH\ssh-keyscan.exe"));
    }

    private sealed class FakeKeyScanLauncher : IOpenSshKeyScanLauncher
    {
        public string Output { get; set; } = string.Empty;

        public Action? OnScan { get; set; }

        public ValueTask<OpenSshKeyScanResult> ScanAsync(
            OpenSshProcessStart start,
            CancellationToken cancellationToken)
        {
            OnScan?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new OpenSshKeyScanResult(Output, string.Empty, 0));
        }
    }

    private sealed class FakeProcessLauncher(FakeOpenSshProcess process)
        : IOpenSshProcessLauncher
    {
        public int LaunchCount { get; private set; }

        public OpenSshProcessStart? LastStart { get; private set; }

        public Exception? LaunchException { get; set; }

        public ValueTask<IOpenSshProcess> LaunchAsync(
            OpenSshProcessStart start,
            CancellationToken cancellationToken)
        {
            LaunchCount++;
            LastStart = start;
            if (LaunchException is not null)
            {
                return ValueTask.FromException<IOpenSshProcess>(LaunchException);
            }

            return ValueTask.FromResult<IOpenSshProcess>(process);
        }
    }

    private sealed class FakeOpenSshProcess : IOpenSshProcess
    {
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        private int _killed;

        public Stream StandardInput { get; } = new MemoryStream();

        public Stream StandardOutput => new ReadNotifyingStream(StandardOutputSource, ReadStarted);

        public Stream StandardError => StandardErrorSource;

        public Stream StandardOutputSource { get; set; } = new MemoryStream([1]);

        public Stream StandardErrorSource { get; set; } = Stream.Null;

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? KillException { get; set; }

        public ManualResetEventSlim? KillGate { get; set; }

        public TaskCompletionSource KillStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ExitBeforeKillException { get; set; }

        public bool WaitBlocks { get; set; }

        public Exception? DisposeException { get; set; }

        public bool HasExited => _exit.Task.IsCompleted;

        public int? ExitCode => HasExited ? _exit.Task.Result : null;

        public int KillCount => _killed;

        public int DisposeCount => _disposed;

        public int WaitCount { get; private set; }

        public void Exit(int exitCode) => _exit.TrySetResult(exitCode);

        public void Kill(bool entireProcessTree)
        {
            Interlocked.Increment(ref _killed);
            KillStarted.TrySetResult();
            KillGate?.Wait();
            if (ExitBeforeKillException)
            {
                _exit.TrySetResult(-1);
            }

            if (KillException is not null)
            {
                throw KillException;
            }

            _exit.TrySetResult(-1);
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCount++;
            if (WaitBlocks)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            await _exit.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            StandardInput.Dispose();
            StandardOutputSource.Dispose();
            StandardErrorSource.Dispose();
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }
    }

    private sealed class ReadNotifyingStream(
        Stream inner,
        TaskCompletionSource readStarted) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult();
            return inner.ReadAsync(buffer, cancellationToken);
        }

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

    private sealed class FakeKnownHostsFactory : IOpenSshKnownHostsFileFactory
    {
        public int CreateCount { get; private set; }

        public int DisposeCount { get; private set; }

        public string? Content { get; private set; }

        public Exception? DisposeException { get; set; }

        public ValueTask<IOpenSshKnownHostsFile> CreateAsync(
            SshHostKeyPin pin,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            Content = OpenSshKnownHosts.Format(pin);
            return ValueTask.FromResult<IOpenSshKnownHostsFile>(
                new FakeKnownHostsFile(this));
        }

        private sealed class FakeKnownHostsFile(FakeKnownHostsFactory owner)
            : IOpenSshKnownHostsFile
        {
            private int _disposed;

            public string Path => @"C:\Temp\strict known_hosts";

            public ValueTask DisposeAsync() =>
                DeleteAsync(CancellationToken.None);

            public ValueTask DeleteAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.DisposeCount++;
                    if (owner.DisposeException is not null)
                    {
                        return ValueTask.FromException(owner.DisposeException);
                    }
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeKnownHostsFile(Exception? deleteException = null)
        : IOpenSshKnownHostsFile
    {
        public string Path => @"C:\Temp\strict known_hosts";

        public int DeleteCount { get; private set; }

        public ValueTask DisposeAsync() =>
            DeleteAsync(CancellationToken.None);

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            DeleteCount++;
            return deleteException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(deleteException);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public int ActiveTimerCount
        {
            get
            {
                lock (_sync)
                {
                    return _timers.Count;
                }
            }
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, _utcNow + dueTime, period);
            lock (_sync)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            _utcNow += amount;
            ManualTimer[] due;
            lock (_sync)
            {
                due = _timers.Where(timer => timer.IsDue(_utcNow)).ToArray();
            }

            foreach (var timer in due)
            {
                timer.Fire(_utcNow);
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_sync)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset dueAt,
            TimeSpan period) : ITimer
        {
            private bool _disposed;
            private DateTimeOffset _dueAt = dueAt;
            private TimeSpan _period = period;

            public bool IsDue(DateTimeOffset now) => !_disposed && now >= _dueAt;

            public void Fire(DateTimeOffset now)
            {
                callback(state);
                if (_period == Timeout.InfiniteTimeSpan)
                {
                    Dispose();
                }
                else
                {
                    _dueAt = now + _period;
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
            {
                _dueAt = owner.GetUtcNow() + dueTime;
                _period = newPeriod;
                return !_disposed;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
