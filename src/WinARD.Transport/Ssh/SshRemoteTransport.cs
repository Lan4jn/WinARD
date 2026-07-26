using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using WinARD.Application.Ports;
using WinARD.Domain.Connections;

namespace WinARD.Transport.Ssh;

public sealed class SshRemoteTransport : IRemoteTransportFactory
{
    private readonly IOpenSshProcessLauncher _processLauncher;
    private readonly IOpenSshKeyScanLauncher _keyScanLauncher;
    private readonly ISshHostKeyPinStore _pinStore;
    private readonly IOpenSshAskPassBroker _askPassBroker;
    private readonly IOpenSshKnownHostsFileFactory _knownHostsFactory;
    private readonly OpenSshExecutablePaths _executables;
    private readonly TransportTimeouts _timeouts;
    private readonly TimeProvider _timeProvider;
    private readonly OpenSshTunnelCleanupOptions _cleanupOptions;

    public SshRemoteTransport(
        ISshHostKeyPinStore pinStore,
        TransportTimeouts? timeouts = null,
        TimeProvider? timeProvider = null)
        : this(
            new SystemOpenSshProcessLauncher(),
            new SystemOpenSshKeyScanLauncher(),
            pinStore,
            new UnsupportedOpenSshAskPassBroker(),
            new TemporaryOpenSshKnownHostsFileFactory(),
            new WindowsOpenSshExecutableResolver(),
            timeouts ?? TransportTimeouts.Default,
            timeProvider ?? TimeProvider.System,
            OpenSshTunnelCleanupOptions.Default)
    {
    }

    public SshRemoteTransport(
        ISshHostKeyPinStore pinStore,
        ICredentialStore credentialStore,
        TransportTimeouts? timeouts = null,
        TimeProvider? timeProvider = null)
        : this(
            new SystemOpenSshProcessLauncher(),
            new SystemOpenSshKeyScanLauncher(),
            pinStore,
            new OpenSshAskPassBroker(credentialStore),
            new TemporaryOpenSshKnownHostsFileFactory(),
            new WindowsOpenSshExecutableResolver(),
            timeouts ?? TransportTimeouts.Default,
            timeProvider ?? TimeProvider.System,
            OpenSshTunnelCleanupOptions.Default)
    {
    }

    public SshRemoteTransport(
        IOpenSshProcessLauncher processLauncher,
        IOpenSshKeyScanLauncher keyScanLauncher,
        ISshHostKeyPinStore pinStore,
        IOpenSshAskPassBroker askPassBroker,
        IOpenSshKnownHostsFileFactory knownHostsFactory,
        IOpenSshExecutableResolver executableResolver,
        TransportTimeouts timeouts,
        TimeProvider timeProvider,
        OpenSshTunnelCleanupOptions? cleanupOptions = null)
    {
        _processLauncher = processLauncher ??
            throw new ArgumentNullException(nameof(processLauncher));
        _keyScanLauncher = keyScanLauncher ??
            throw new ArgumentNullException(nameof(keyScanLauncher));
        _pinStore = pinStore ?? throw new ArgumentNullException(nameof(pinStore));
        _askPassBroker = askPassBroker ??
            throw new ArgumentNullException(nameof(askPassBroker));
        _knownHostsFactory = knownHostsFactory ??
            throw new ArgumentNullException(nameof(knownHostsFactory));
        ArgumentNullException.ThrowIfNull(executableResolver);
        _executables = executableResolver.Resolve();
        _timeouts = timeouts ?? throw new ArgumentNullException(nameof(timeouts));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _cleanupOptions = cleanupOptions ?? OpenSshTunnelCleanupOptions.Default;
    }

    public async Task<TransportConnection> ConnectAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = new CancellationTokenSource(_timeouts.Connection, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);

        try
        {
            return await ConnectCoreAsync(profile, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TransportTimeoutException(TransportTimeoutStage.Connection);
        }
    }

    private async Task<TransportConnection> ConnectCoreAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var sshProfile = profile.SshProfile ??
            throw new ArgumentException("An SSH transport requires an SSH profile.", nameof(profile));
        var endpoint = new SshHostKeyEndpoint(sshProfile.Host, sshProfile.Port);

        await _askPassBroker
            .EnsureSupportedAsync(sshProfile, cancellationToken)
            .ConfigureAwait(false);

        var scanStart = OpenSshCommandBuilder.BuildKeyScan(
            _executables.KeyScanPath,
            endpoint,
            _timeouts.Connection);
        var scan = await _keyScanLauncher
            .ScanAsync(scanStart, cancellationToken)
            .ConfigureAwait(false);
        var candidates = OpenSshKeyScanParser.Parse(scan.StandardOutput, endpoint);
        var pin = await _pinStore.FindAsync(endpoint, cancellationToken).ConfigureAwait(false) ??
            EndpointPin(sshProfile, endpoint);
        var trustedCandidate = VerifyHostKey(endpoint, candidates, pin);

        var knownHosts = await _knownHostsFactory
            .CreateAsync(trustedCandidate.ToPin(), cancellationToken)
            .ConfigureAwait(false);
        IOpenSshProcess? process = null;
        IOpenSshAskPassSession? askPassSession = null;
        try
        {
            askPassSession = await _askPassBroker
                .PrepareAsync(sshProfile, cancellationToken)
                .ConfigureAwait(false);
            var processStart = OpenSshCommandBuilder.BuildTunnel(
                _executables.SshPath,
                sshProfile,
                knownHosts.Path);
            processStart = askPassSession.Configure(processStart);
            process = await _processLauncher
                .LaunchAsync(processStart, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            List<Exception>? cleanupFailures = null;
            if (process is not null)
            {
                try
                {
                    await process.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    TemporaryOpenSshKnownHostsFile.AddException(
                        ref cleanupFailures,
                        cleanupException);
                }
            }

            if (askPassSession is not null)
            {
                try
                {
                    await askPassSession.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    TemporaryOpenSshKnownHostsFile.AddException(
                        ref cleanupFailures,
                        cleanupException);
                }
            }

            try
            {
                await knownHosts.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                TemporaryOpenSshKnownHostsFile.AddException(
                    ref cleanupFailures,
                    cleanupException);
            }

            RethrowWithCleanupFailures(exception, cleanupFailures);
            throw new InvalidOperationException("Unreachable.");
        }

        var diagnostics = OpenSshDiagnostics.DrainAsync(
            process.StandardError,
            cancellationToken);
        var lifetime = new OpenSshTunnelLifetime(
            process,
            knownHosts,
            diagnostics,
            _cleanupOptions,
            _timeProvider,
            askPassSession);
        try
        {
            var firstByte = new byte[1];
            var read = await process.StandardOutput
                .ReadAsync(firstByte, cancellationToken)
                .ConfigureAwait(false);
            if (read == 1)
            {
                var stream = new PrefixDuplexStream(
                    firstByte[0],
                    process.StandardOutput,
                    process.StandardInput);
                return new TransportConnection(
                    stream,
                    new EndPointDescription(sshProfile.TargetHost, sshProfile.TargetPort),
                    lifetime);
            }

            if (!process.HasExited)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            var summary = await diagnostics.ConfigureAwait(false);
            throw new OpenSshTunnelException(
                process.ExitCode,
                summary);
        }
        catch (Exception exception)
        {
            try
            {
                await lifetime.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(exception, cleanupException);
            }

            ExceptionDispatchInfo.Capture(exception).Throw();
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private static SshHostKeyCandidate VerifyHostKey(
        SshHostKeyEndpoint endpoint,
        IReadOnlyList<SshHostKeyCandidate> candidates,
        SshHostKeyPin? pin)
    {
        if (pin is null)
        {
            var verification = SshHostKeyVerifier.Verify(candidates[0], pin: null);
            throw new SshHostKeyUnknownException(verification);
        }

        foreach (var candidate in candidates)
        {
            if (SshHostKeyVerifier.Verify(candidate, pin).Status == SshHostKeyStatus.Trusted)
            {
                return candidate;
            }
        }

        throw new SshHostKeyChangedException(
            SshHostKeyVerifier.Verify(candidates[0], pin));
    }

    private static SshHostKeyPin? EndpointPin(
        SshProfile profile,
        SshHostKeyEndpoint endpoint) =>
        profile.HostKeyPin?.Endpoint == endpoint
            ? profile.HostKeyPin
            : null;

    private static void RethrowWithCleanupFailures(
        Exception primaryException,
        List<Exception>? cleanupFailures)
    {
        if (cleanupFailures is null)
        {
            ExceptionDispatchInfo.Capture(primaryException).Throw();
        }

        throw new AggregateException([primaryException, .. cleanupFailures]);
    }
}

public interface IOpenSshAskPassBroker
{
    ValueTask EnsureSupportedAsync(
        SshProfile profile,
        CancellationToken cancellationToken);

    ValueTask<IOpenSshAskPassSession> PrepareAsync(
        SshProfile profile,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IOpenSshAskPassSession>(
            NoOpenSshAskPassSession.Instance);
}

public sealed class UnsupportedOpenSshAskPassBroker : IOpenSshAskPassBroker
{
    public ValueTask EnsureSupportedAsync(
        SshProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        if (profile.CredentialReference is not null)
        {
            throw new OpenSshAuthenticationUnsupportedException();
        }

        return ValueTask.CompletedTask;
    }
}

public static partial class OpenSshDiagnostics
{
    public const int MaximumSummaryLength = 4096;

    internal static async Task<string> DrainAsync(
        Stream standardError,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            standardError,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        var retained = new StringBuilder(MaximumSummaryLength + 512);
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var available = retained.Capacity - retained.Length;
            if (available > 0)
            {
                retained.Append(buffer, 0, Math.Min(read, available));
            }
        }

        return Sanitize(retained.ToString(), MaximumSummaryLength);
    }

    internal static string Sanitize(string text, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        var sanitized = SensitiveAssignment().Replace(text, "$1=[REDACTED]");
        sanitized = UserKnownHostsFileAssignment().Replace(
            sanitized,
            "$1=[REDACTED]");
        sanitized = IdentityFilePath().Replace(sanitized, "$1 [REDACTED]");
        sanitized = KnownHostsPath().Replace(sanitized, "[REDACTED]");
        return sanitized.Length <= maximumLength
            ? sanitized
            : sanitized[..maximumLength];
    }

    [GeneratedRegex(
        @"(?i)(password|passphrase|token|secret)\s*=\s*([^\s]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignment();

    [GeneratedRegex(
        @"(?i)(UserKnownHostsFile)\s*=\s*(?:""[^""]*""|'[^']*'|[^\s;]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex UserKnownHostsFileAssignment();

    [GeneratedRegex(
        @"(?i)(identity\s+file|load\s+key)\s+(?:""[^""]+""|'[^']+'|[^\s;]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdentityFilePath();

    [GeneratedRegex(
        @"(?i)(?:""[^""]*\.known_hosts""|'[^']*\.known_hosts'|(?:[a-z]:\\|/)[^\r\n;]*?\.known_hosts)",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownHostsPath();
}

public sealed class OpenSshTunnelException : Exception
{
    public OpenSshTunnelException(int? exitCode, string diagnosticSummary)
        : base(CreateMessage(exitCode))
    {
        ExitCode = exitCode;
        DiagnosticSummary = diagnosticSummary ?? string.Empty;
    }

    public int? ExitCode { get; }

    public string DiagnosticSummary { get; }

    private static string CreateMessage(int? exitCode)
    {
        var code = exitCode is null
            ? "unknown"
            : exitCode.Value.ToString(CultureInfo.InvariantCulture);
        return $"OpenSSH failed to establish the remote tunnel (exit code {code}).";
    }
}

public sealed record OpenSshTunnelCleanupOptions
{
    public static OpenSshTunnelCleanupOptions Default { get; } =
        new(TimeSpan.FromSeconds(2));

    public OpenSshTunnelCleanupOptions(TimeSpan cleanupTimeout)
    {
        if (cleanupTimeout <= TimeSpan.Zero ||
            cleanupTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cleanupTimeout),
                "Cleanup timeout must be finite and positive.");
        }

        CleanupTimeout = cleanupTimeout;
    }

    public TimeSpan CleanupTimeout { get; }
}

public sealed class OpenSshTunnelCleanupTimeoutException : TimeoutException
{
    public OpenSshTunnelCleanupTimeoutException(TimeSpan timeout)
        : base($"OpenSSH tunnel cleanup exceeded {timeout}.")
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}

public sealed class OpenSshAuthenticationUnsupportedException : Exception
{
    public OpenSshAuthenticationUnsupportedException()
        : base(
            "Credential-backed OpenSSH password or passphrase authentication is not supported yet.")
    {
    }
}

public sealed class SshHostKeyUnknownException : Exception
{
    public SshHostKeyUnknownException(SshHostKeyVerification verification)
        : base(
            $"The SSH host key for {verification.Endpoint.Host}:{verification.Endpoint.Port} is not pinned.")
    {
        Verification = verification ?? throw new ArgumentNullException(nameof(verification));
    }

    public SshHostKeyVerification Verification { get; }
}

internal sealed class PrefixDuplexStream(
    byte prefix,
    Stream standardOutput,
    Stream standardInput) : Stream
{
    private int _prefixAvailable = 1;

    public override bool CanRead => standardOutput.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => standardInput.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => standardInput.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        standardInput.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        if (count > 0 && Interlocked.Exchange(ref _prefixAvailable, 0) == 1)
        {
            buffer[offset] = prefix;
            return 1;
        }

        return standardOutput.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        if (!buffer.IsEmpty && Interlocked.Exchange(ref _prefixAvailable, 0) == 1)
        {
            buffer[0] = prefix;
            return 1;
        }

        return standardOutput.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (!buffer.IsEmpty && Interlocked.Exchange(ref _prefixAvailable, 0) == 1)
        {
            buffer.Span[0] = prefix;
            return ValueTask.FromResult(1);
        }

        return standardOutput.ReadAsync(buffer, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        standardInput.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) =>
        standardInput.Write(buffer);

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        standardInput.WriteAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => base.DisposeAsync();

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("Offset and count exceed the buffer length.");
        }
    }
}

internal sealed class OpenSshTunnelLifetime(
    IOpenSshProcess process,
    IOpenSshKnownHostsFile knownHosts,
    Task<string> diagnostics,
    OpenSshTunnelCleanupOptions cleanupOptions,
    TimeProvider timeProvider,
    IOpenSshAskPassSession? askPassSession = null) : IAsyncDisposable
{
    private readonly object _sync = new();
    private Task? _disposeTask;

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_sync)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        var cleanupState = new CleanupState();
        using var cleanupCancellation = new CancellationTokenSource(
            cleanupOptions.CleanupTimeout,
            timeProvider);
        var cleanupToken = cleanupCancellation.Token;

        await RunStepAsync(
            _ =>
            {
                process.StandardInput.Dispose();
                return Task.CompletedTask;
            },
            ignoreOperationCancellation: false,
            suppressException: null,
            cleanupState,
            cleanupToken).ConfigureAwait(false);
        if (!process.HasExited)
        {
            await RunStepAsync(
                _ => Task.Run(
                    () => process.Kill(entireProcessTree: true),
                    CancellationToken.None),
                ignoreOperationCancellation: false,
                suppressException: _ => process.HasExited,
                cleanupState,
                cleanupToken).ConfigureAwait(false);
        }

        await RunStepAsync(
            token => process.WaitForExitAsync(token),
            ignoreOperationCancellation: false,
            suppressException: null,
            cleanupState,
            cleanupToken).ConfigureAwait(false);
        await RunStepAsync(
            _ => diagnostics,
            ignoreOperationCancellation: true,
            suppressException: null,
            cleanupState,
            cleanupToken).ConfigureAwait(false);
        await RunStepAsync(
            _ => process.DisposeAsync().AsTask(),
            ignoreOperationCancellation: false,
            suppressException: null,
            cleanupState,
            cleanupToken).ConfigureAwait(false);
        if (askPassSession is not null)
        {
            await RunStepAsync(
                _ => askPassSession.DisposeAsync().AsTask(),
                ignoreOperationCancellation: false,
                suppressException: null,
                cleanupState,
                cleanupToken).ConfigureAwait(false);
        }

        await RunStepAsync(
            token => knownHosts.DeleteAsync(token).AsTask(),
            ignoreOperationCancellation: false,
            suppressException: null,
            cleanupState,
            cleanupToken).ConfigureAwait(false);

        TemporaryOpenSshKnownHostsFile.ThrowIfAny(cleanupState.Failures);
    }

    private async Task RunStepAsync(
        Func<CancellationToken, Task> beginOperation,
        bool ignoreOperationCancellation,
        Func<Exception, bool>? suppressException,
        CleanupState cleanupState,
        CancellationToken cleanupToken)
    {
        Task operation;
        try
        {
            operation = beginOperation(cleanupToken);
        }
        catch (Exception exception)
        {
            if (suppressException?.Invoke(exception) == true)
            {
                return;
            }

            TemporaryOpenSshKnownHostsFile.AddException(
                ref cleanupState.Failures,
                exception);
            return;
        }

        try
        {
            if (operation.IsCompleted)
            {
                await operation.ConfigureAwait(false);
            }
            else
            {
                await operation.WaitAsync(cleanupToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cleanupToken.IsCancellationRequested)
        {
            ObserveFault(operation);
            if (!cleanupState.TimeoutRecorded)
            {
                cleanupState.TimeoutRecorded = true;
                TemporaryOpenSshKnownHostsFile.AddException(
                    ref cleanupState.Failures,
                    new OpenSshTunnelCleanupTimeoutException(
                        cleanupOptions.CleanupTimeout));
            }
        }
        catch (OperationCanceledException) when (ignoreOperationCancellation)
        {
        }
        catch (Exception exception)
        {
            if (suppressException?.Invoke(exception) == true)
            {
                return;
            }

            TemporaryOpenSshKnownHostsFile.AddException(
                ref cleanupState.Failures,
                exception);
        }
    }

    private static void ObserveFault(Task operation)
    {
        _ = operation.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class CleanupState
    {
        public bool TimeoutRecorded { get; set; }

        public List<Exception>? Failures;
    }
}
