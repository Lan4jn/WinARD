using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Application.Sessions;
using WinARD.Desktop.Services;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Domain.Sessions;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;
using WinARD.Transport;
using WinARD.Transport.Ssh;
using Xunit;

namespace WinARD.Desktop.Tests.Services;

public sealed class ConnectionFailureDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"winard-failure-diagnostics-{Guid.NewGuid():N}");

    [Fact]
    public async Task CompatibilityFailureRetriesOnceWithANewFallbackConnectionAfterCleanup()
    {
        var events = new List<string>();
        var clients = new Queue<RetryClient>(
        [
            new RetryClient("preferred", events, QualityBootstrapFailureReason.DecoderFailure),
            new RetryClient("fallback", events),
        ]);
        var handler = new ConnectDeviceHandler(
            new RetryTransportFactory(events),
            new RetrySecretProvider(events),
            new RetryClientFactory(clients, events),
            new ErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(handler, new CancelPrompt());

        var outcome = await workflow.AttemptAsync(
            Profile(), stageChanged: null, acceptedHostKey: null, CancellationToken.None);

        Assert.NotNull(outcome.Result.Session);
        Assert.Equal(2, events.Count(value => value.StartsWith("client-create", StringComparison.Ordinal)));
        Assert.True(events.IndexOf("preferred-client-dispose") < events.IndexOf("client-create-fallback"));
        Assert.True(events.IndexOf("preferred-transport-dispose") < events.IndexOf("client-create-fallback"));
        Assert.True(events.IndexOf("preferred-secret-dispose") < events.IndexOf("client-create-fallback"));
        Assert.Contains("preferred-configure-Preferred-Rgb565", events);
        Assert.Contains("fallback-configure-Fallback-Bgra32", events);
        Assert.True(outcome.Result.Session!.BootstrapState.FallbackUsed);
        Assert.Equal(QualityBootstrapAttempt.Fallback, outcome.Result.Session.BootstrapState.Attempt);
        Assert.Equal(
            QualityBootstrapFailureReason.DecoderFailure,
            outcome.Result.Session.BootstrapState.PreferredFailureReason);
        await outcome.Result.Session.DisposeAsync();
    }

    [Fact]
    public async Task FallbackFailureIsReturnedWithoutAThirdConnection()
    {
        var events = new List<string>();
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        var clients = new Queue<RetryClient>(
        [
            new RetryClient("preferred", events, QualityBootstrapFailureReason.UnsupportedEncoding),
            new RetryClient("fallback", events, QualityBootstrapFailureReason.MalformedFramebufferUpdate),
        ]);
        var handler = new ConnectDeviceHandler(
            new RetryTransportFactory(events),
            new RetrySecretProvider(events),
            new RetryClientFactory(clients, events),
            new BootstrapReasonErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(handler, new CancelPrompt(), sink);

        var outcome = await workflow.AttemptAsync(
            Profile(), stageChanged: null, acceptedHostKey: null, CancellationToken.None);

        Assert.Null(outcome.Result.Session);
        Assert.Equal(2, events.Count(value => value.StartsWith("client-create", StringComparison.Ordinal)));
        Assert.Empty(clients);
        Assert.Equal("BOOTSTRAP_MALFORMEDFRAMEBUFFERUPDATE", outcome.Result.Error!.Code);
        var diagnostics = sink.Snapshot();
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal("QUALITY_BOOTSTRAP_FALLBACK", diagnostics[0].Code);
        Assert.Equal(
            new[]
            {
                ("BootstrapAttempt", "Preferred"),
                ("BootstrapFallbackReason", "UnsupportedEncoding"),
            },
            diagnostics[0].Fields.Select(field => (field.Name, field.Value)));
        Assert.Equal("QUALITY_BOOTSTRAP_FALLBACK", diagnostics[1].Code);
        Assert.Null(diagnostics[1].Exception);
        Assert.Equal(
            new[]
            {
                ("BootstrapAttempt", "Fallback"),
                ("FallbackFailed", "True"),
                ("PreferredFailureReason", "UnsupportedEncoding"),
                ("BootstrapFallbackReason", "MalformedFramebufferUpdate"),
            },
            diagnostics[1].Fields.Select(field => (field.Name, field.Value)));
    }

    [Fact]
    public async Task NonCompatibilityFailureDoesNotRetryWithFallback()
    {
        var events = new List<string>();
        var clients = new Queue<RetryClient>(
        [
            new RetryClient("preferred", events, bootstrapFailure: new InvalidOperationException("not compatible")),
            new RetryClient("unused", events),
        ]);
        var handler = new ConnectDeviceHandler(
            new RetryTransportFactory(events),
            new RetrySecretProvider(events),
            new RetryClientFactory(clients, events),
            new ErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(handler, new CancelPrompt());

        var outcome = await workflow.AttemptAsync(
            Profile(), stageChanged: null, acceptedHostKey: null, CancellationToken.None);

        Assert.Null(outcome.Result.Session);
        Assert.Single(events, value => value.StartsWith("client-create", StringComparison.Ordinal));
        Assert.Single(clients);
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotRetryWithFallback()
    {
        var events = new List<string>();
        var clients = new Queue<RetryClient>(
        [
            new RetryClient(
                "preferred",
                events,
                authenticationFailure: new ArdAuthenticationRejectedException(1, null, false)),
            new RetryClient("unused", events),
        ]);
        var handler = new ConnectDeviceHandler(
            new RetryTransportFactory(events),
            new RetrySecretProvider(events),
            new RetryClientFactory(clients, events),
            new ErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(handler, new CancelPrompt());

        var outcome = await workflow.AttemptAsync(
            Profile(), stageChanged: null, acceptedHostKey: null, CancellationToken.None);

        Assert.Null(outcome.Result.Session);
        Assert.Single(events, value => value.StartsWith("client-create", StringComparison.Ordinal));
        Assert.Single(clients);
    }

    [Theory]
    [MemberData(nameof(NonCompatibilityTransportFailures))]
    public async Task TransportFailureDoesNotRetryWithFallback(Exception failure, string expectedCode)
    {
        var transport = new FailingTransportFactory(failure);
        var handler = new ConnectDeviceHandler(
            transport,
            new RetrySecretProvider([]),
            new RetryClientFactory(new Queue<RetryClient>(), []),
            new ErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(handler, new CancelPrompt());

        var outcome = await workflow.AttemptAsync(
            Profile(), stageChanged: null, acceptedHostKey: null, CancellationToken.None);

        Assert.Null(outcome.Result.Session);
        Assert.Equal(expectedCode, outcome.Result.Error!.Code);
        Assert.Equal(1, transport.Attempts);
    }

    [Fact]
    public async Task AcceptedHostKeyThenCompatibilityFailureStopsAfterTwoTransportsWithoutFallback()
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "AQIDBA==");
        var events = new List<string>();
        var transport = new HostKeyBootstrapTransport(candidate, events);
        var clients = new Queue<RetryClient>(
        [
            new RetryClient("preferred", events, QualityBootstrapFailureReason.DecoderFailure),
            new RetryClient("fallback", events),
        ]);
        var handler = new ConnectDeviceHandler(
            transport,
            new RetrySecretProvider(events),
            new RetryClientFactory(clients, events),
            new ErrorMapper());
        var prompt = new TrustPrompt();
        var workflow = new ConnectionAttemptWorkflow(handler, prompt);
        var profile = Profile().WithSsh(WinARD.Domain.Connections.SshProfile.Create(
            endpoint.Host,
            endpoint.Port,
            "ssh-user",
            privateKeyPath: null,
            targetHost: "studio.local",
            targetPort: 5900,
            credentialReference: null,
            pinnedHostKeyAlgorithm: null,
            pinnedHostKeySha256: null));

        var outcome = await workflow.AttemptAsync(
            profile, stageChanged: null, acceptedHostKey: (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Null(outcome.Result.Session);
        Assert.Equal(2, transport.Attempts);
        Assert.Equal(1, prompt.Count);
        Assert.True(events.IndexOf("transport-hostkey") < events.IndexOf("transport-preferred"));
        Assert.True(events.IndexOf("transport-preferred") < events.IndexOf("client-create-preferred"));
        Assert.DoesNotContain("transport-fallback", events);
    }

    [Fact]
    public async Task FallbackHostKeyFailureStopsAtTwoTransports()
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "AQIDBA==");
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        var events = new List<string>();
        var transport = new FallbackHostKeyTransport(candidate, events);
        var clients = new Queue<RetryClient>(
        [
            new RetryClient("preferred", events, QualityBootstrapFailureReason.DecoderFailure),
            new RetryClient("fallback", events),
        ]);
        var handler = new ConnectDeviceHandler(
            transport,
            new RetrySecretProvider(events),
            new RetryClientFactory(clients, events),
            new ErrorMapper());
        var prompt = new TrustPrompt();
        var workflow = new ConnectionAttemptWorkflow(handler, prompt, sink);

        var outcome = await workflow.AttemptAsync(
            ProfileWithSsh(candidate.Endpoint), stageChanged: null,
            acceptedHostKey: (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.Null(outcome.Result.Session);
        Assert.Equal(2, transport.Attempts);
        Assert.Equal(0, prompt.Count);
        Assert.Single(events, value => value.StartsWith("client-create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedFallbackHostKeyWritesOneTerminalFallbackFailureDiagnostic()
    {
        var endpoint = new SshHostKeyEndpoint("jump.local", 22);
        var candidate = SshHostKeyVerifier.CreateCandidate(endpoint, "ssh-ed25519", "AQIDBA==");
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        var events = new List<string>();
        var transport = new FallbackHostKeyTransport(candidate, events);
        var clients = new Queue<RetryClient>(
        [
            new RetryClient("preferred", events, QualityBootstrapFailureReason.DecoderFailure),
        ]);
        var handler = new ConnectDeviceHandler(
            transport,
            new RetrySecretProvider(events),
            new RetryClientFactory(clients, events),
            new ErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(handler, new CancelPrompt(), sink);

        var outcome = await workflow.AttemptAsync(
            ProfileWithSsh(candidate.Endpoint), stageChanged: null,
            acceptedHostKey: null, CancellationToken.None);

        Assert.Null(outcome.Result.Session);
        Assert.Equal(2, transport.Attempts);
        var terminal = Assert.Single(
            sink.Snapshot(),
            item => item.Fields.Any(field => field.Name == "FallbackFailed" && field.Value == "True"));
        Assert.Equal("QUALITY_BOOTSTRAP_FALLBACK", terminal.Code);
        Assert.Null(terminal.Exception);
        Assert.Contains(terminal.Fields, field =>
            field.Name == "PreferredFailureReason" && field.Value == "DecoderFailure");
    }

    public static TheoryData<Exception, string> NonCompatibilityTransportFailures => new()
    {
        { new SocketException((int)SocketError.HostNotFound), "DNS_RESOLUTION_FAILED" },
        { new SocketException((int)SocketError.ConnectionRefused), "TCP_CONNECTION_FAILED" },
        { new OpenSshTunnelException(255, "raw ssh marker"), "SSH_CONNECTION_FAILED" },
        { new TransportTimeoutException(TransportTimeoutStage.Connection), "TRANSPORT_TIMEOUT" },
    };

    private static ConnectionProfile ProfileWithSsh(SshHostKeyEndpoint endpoint) => Profile().WithSsh(
        WinARD.Domain.Connections.SshProfile.Create(
            endpoint.Host,
            endpoint.Port,
            "ssh-user",
            privateKeyPath: null,
            targetHost: "studio.local",
            targetPort: 5900,
            credentialReference: null,
            pinnedHostKeyAlgorithm: null,
            pinnedHostKeySha256: null));

    [Fact]
    public async Task UserCancellationPreservesTokenAndDoesNotRetryWithFallback()
    {
        var events = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var clients = new Queue<RetryClient>(
        [
            new RetryClient("preferred", events, cancelBeforeBootstrap: cancellation),
            new RetryClient("unused", events),
        ]);
        var handler = new ConnectDeviceHandler(
            new RetryTransportFactory(events),
            new RetrySecretProvider(events),
            new RetryClientFactory(clients, events),
            new ErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(handler, new CancelPrompt());

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => workflow.AttemptAsync(
            Profile(), stageChanged: null, acceptedHostKey: null, cancellation.Token));

        Assert.Equal(cancellation.Token, thrown.CancellationToken);
        Assert.Single(events, value => value.StartsWith("client-create", StringComparison.Ordinal));
        Assert.Single(clients);
    }

    [Fact]
    public async Task PreferredSuccessDoesNotOpenFallbackConnection()
    {
        var events = new List<string>();
        var clients = new Queue<RetryClient>(
        [
            new RetryClient("preferred", events),
            new RetryClient("unused", events),
        ]);
        var handler = new ConnectDeviceHandler(
            new RetryTransportFactory(events),
            new RetrySecretProvider(events),
            new RetryClientFactory(clients, events),
            new ErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(handler, new CancelPrompt());

        var outcome = await workflow.AttemptAsync(
            Profile(), stageChanged: null, acceptedHostKey: null, CancellationToken.None);

        Assert.NotNull(outcome.Result.Session);
        Assert.Single(events, value => value.StartsWith("client-create", StringComparison.Ordinal));
        Assert.False(outcome.Result.Session!.BootstrapState.FallbackUsed);
        Assert.Null(outcome.Result.Session.BootstrapState.PreferredFailureReason);
        Assert.Single(clients);
        await outcome.Result.Session.DisposeAsync();
    }

    [Fact]
    public async Task BootstrapFallbackDiagnosticContainsOnlyClosedSafeFields()
    {
        const string rawMarker = "private-host-payload-marker";
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        var events = new List<string>();
        var clients = new Queue<RetryClient>(
        [
            new RetryClient("preferred", events, QualityBootstrapFailureReason.RemoteSessionClosed),
            new RetryClient("fallback", events),
        ]);
        var handler = new ConnectDeviceHandler(
            new RetryTransportFactory(events),
            new RetrySecretProvider(events),
            new RetryClientFactory(clients, events),
            new ErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(handler, new CancelPrompt(), sink);

        var outcome = await workflow.AttemptAsync(
            ConnectionProfile.Create(
                Guid.NewGuid(), rawMarker, $"{rawMarker}.invalid", 5900, rawMarker),
            stageChanged: null,
            acceptedHostKey: null,
            CancellationToken.None);

        var diagnostic = Assert.Single(sink.Snapshot());
        Assert.Equal("QUALITY_BOOTSTRAP_FALLBACK", diagnostic.Code);
        Assert.Equal(
            new[]
            {
                ("BootstrapAttempt", "Preferred"),
                ("BootstrapFallbackReason", "RemoteSessionClosed"),
            },
            diagnostic.Fields.Select(field => (field.Name, field.Value)));
        Assert.Null(diagnostic.Exception);
        Assert.DoesNotContain(rawMarker, JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
        await outcome.Result.Session!.DisposeAsync();
    }

    [Fact]
    public async Task HandshakeFailureExportsOnlySafeStageAndByteCounts()
    {
        const string secretMarker = "raw-banner-secret-marker";
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        var secret = new TrackingSecret("password");
        var transportLifetime = new TrackingLifetime();
        var failure = RfbProtocolException.Create(
            $"Malformed banner: {secretMarker}",
            new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.TruncatedRead,
                HandshakeStage: RfbHandshakeStage.VersionBanner,
                ExpectedByteCount: 12,
                ActualByteCount: 10));
        var handler = new ConnectDeviceHandler(
            new TransportFactory(transportLifetime),
            new SecretProvider(new RegisteredSecret(secret, redactor)),
            new ClientFactory(new NegotiationFailingClient(failure)),
            new ErrorMapper());
        var workflow = new ConnectionAttemptWorkflow(handler, new CancelPrompt(), sink);

        _ = await workflow.AttemptAsync(
            Profile(),
            stageChanged: null,
            acceptedHostKey: null,
            CancellationToken.None);

        var diagnostic = Assert.Single(sink.Snapshot());
        Assert.Equal(
            new[]
            {
                ("stage", "Negotiating"),
                ("ProtocolFailureKind", "TruncatedRead"),
                ("RfbHandshakeStage", "VersionBanner"),
                ("ExpectedByteCount", "12"),
                ("ActualByteCount", "10"),
            },
            diagnostic.Fields.Select(field => (field.Name, field.Value)));
        Assert.DoesNotContain(
            diagnostic.Fields,
            field =>
                field.Name.Contains("Banner", StringComparison.OrdinalIgnoreCase) &&
                field.Name != "RfbHandshakeStage" ||
                field.Value.Contains(secretMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(secretMarker, JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArdOuterLengthFailureWithZeroCiphertextLengthSurvivesDiagnosticExport()
    {
        await using var inner = new MemoryStream([0, 0], writable: true);
        await using var encrypted = new ArdEncryptedStream(inner, ProtocolLimits.Default);
        encrypted.Activate(new ArdSessionCipherMaterial(new byte[16], new byte[16]));
        var failure = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            encrypted.ReadAsync(new byte[1]).AsTask());
        Assert.Equal(ArdEncryptedPacketFailureStage.OuterLength, failure.Failure?.ArdEncryptionStage);
        Assert.Equal(0, failure.Failure?.ArdCiphertextLength);

        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        var workflow = new ConnectionAttemptWorkflow(
            new ConnectDeviceHandler(
                new TransportFactory(new TrackingLifetime()),
                new SecretProvider(new TrackingSecret("password")),
                new ClientFactory(new NegotiationFailingClient(failure)),
                new ErrorMapper()),
            new CancelPrompt(),
            sink);
        _ = await workflow.AttemptAsync(
            Profile(), stageChanged: null, acceptedHostKey: null, CancellationToken.None);

        Directory.CreateDirectory(_directory);
        var archivePath = Path.Combine(_directory, "ard-zero-outer-length.zip");
        using var exporter = new DiagnosticExporter(sink, redactor);
        await exporter.ExportAsync(archivePath, DiagnosticExportContext.Empty, CancellationToken.None);

        using var archive = ZipFile.OpenRead(archivePath);
        using var document = JsonDocument.Parse(ReadEntry(archive.GetEntry("diagnostics.json")!));
        var fields = document.RootElement.GetProperty("events")[0].GetProperty("fields");
        Assert.Equal("OuterLength", fields.GetProperty("ArdEncryptionStage").GetString());
        Assert.Equal("0", fields.GetProperty("ArdEncryptedPacketLength").GetString());
    }

    [Fact]
    public async Task AuthenticationFailureIsRecordedBeforeRegisteredSecretIsReleased()
    {
        const string secretValue = "密碼🔐-diagnostic-secret";
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);
        var secret = new TrackingSecret(secretValue);
        var client = new FailingClient(FailureMessage(secretValue));
        var transportLifetime = new TrackingLifetime();
        var workflow = Workflow(redactor, sink, secret, client, transportLifetime);

        var outcome = await workflow.AttemptAsync(
            Profile(),
            stageChanged: null,
            acceptedHostKey: null,
            CancellationToken.None);

        Assert.NotNull(outcome.Result.Error);
        Assert.Equal(0, redactor.RegisteredVariantCount);
        Assert.Equal(1, secret.DisposeCount);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, transportLifetime.DisposeCount);
        AssertContainsNoSecretVariant(JsonSerializer.Serialize(sink.Snapshot()), secretValue);

        Directory.CreateDirectory(_directory);
        var archivePath = Path.Combine(_directory, "diagnostics.zip");
        using var exporter = new DiagnosticExporter(sink, redactor);
        await exporter.ExportAsync(archivePath, DiagnosticExportContext.Empty, CancellationToken.None);

        using var archive = ZipFile.OpenRead(archivePath);
        var exported = string.Join('\n', archive.Entries.Select(ReadEntry));
        AssertContainsNoSecretVariant(exported, secretValue);
    }

    [Fact]
    public async Task ThrowingDiagnosticSinkDoesNotReplaceConnectionErrorOrSkipCleanup()
    {
        const string secretValue = "observer-cleanup-secret";
        using var redactor = new SecretRedactor();
        var secret = new TrackingSecret(secretValue);
        var client = new FailingClient(FailureMessage(secretValue));
        var transportLifetime = new TrackingLifetime();
        var workflow = Workflow(redactor, new ThrowingSink(), secret, client, transportLifetime);

        var outcome = await workflow.AttemptAsync(
            Profile(),
            stageChanged: null,
            acceptedHostKey: null,
            CancellationToken.None);

        Assert.NotNull(outcome.Result.Error);
        Assert.Equal(SessionState.Failed, outcome.Result.State);
        Assert.Equal(0, redactor.RegisteredVariantCount);
        Assert.Equal(1, secret.DisposeCount);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(1, transportLifetime.DisposeCount);
    }

    [Fact]
    public void RegisteredSecretDisposesTransferredSecretWhenConstructionFailsBeforeRegistration()
    {
        using var redactor = new SecretRedactor();
        var secret = new ThrowingLengthSecret();

        Assert.Throws<InvalidOperationException>(() => new RegisteredSecret(secret, redactor));

        Assert.Equal(1, secret.DisposeCount);
        Assert.Equal(0, redactor.RegisteredSecretCount);
    }

    private static ConnectionAttemptWorkflow Workflow(
        SecretRedactor redactor,
        ISafeDiagnosticSink sink,
        TrackingSecret secret,
        FailingClient client,
        TrackingLifetime transportLifetime)
    {
        var handler = new ConnectDeviceHandler(
            new TransportFactory(transportLifetime),
            new SecretProvider(new RegisteredSecret(secret, redactor)),
            new ClientFactory(client),
            new ErrorMapper());
        return new ConnectionAttemptWorkflow(
            handler,
            new CancelPrompt(),
            sink);
    }

    private static ConnectionProfile Profile() => ConnectionProfile.Create(
        Guid.NewGuid(), "Studio", "studio.local", 5900, "operator");

    private static string FailureMessage(string secret) => string.Join('|',
        secret,
        Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)),
        Uri.EscapeDataString(secret),
        JsonSerializer.Serialize(secret)[1..^1]);

    private static void AssertContainsNoSecretVariant(string actual, string secret)
    {
        Assert.DoesNotContain(secret, actual, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), actual, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.EscapeDataString(secret), actual, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonSerializer.Serialize(secret)[1..^1], actual, StringComparison.Ordinal);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class TransportFactory(TrackingLifetime lifetime) : IRemoteTransportFactory
    {
        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken) => Task.FromResult(new TransportConnection(
                new MemoryStream(),
                new EndPointDescription(profile.Host, profile.Port),
                lifetime));
    }

    private sealed class SecretProvider(ISecret secret) : IConnectionSecretProvider
    {
        public ValueTask<ISecret> GetSecretAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken) => ValueTask.FromResult(secret);
    }

    private sealed class ClientFactory(IRfbClient client) : IRfbClientFactory
    {
        public IRfbClient Create(Stream stream) => client;
    }

    private sealed class FailingClient(string message) : IRfbClient
    {
        public int DisposeCount { get; private set; }

        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AuthenticateAsync(
            string username,
            ISecret secret,
            CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException(message));

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NegotiationFailingClient(Exception exception) : IRfbClient
    {
        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.FromException(exception);
        public Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingSecret(string value) : ISecret
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(value);

        public int DisposeCount { get; private set; }
        public int Length => _bytes.Length;
        public void CopyTo(Span<byte> destination) => _bytes.CopyTo(destination);
        public ISecret Clone() => new TrackingSecret(value);
        public void Dispose() => DisposeCount++;
    }

    private sealed class TrackingLifetime : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingLengthSecret : ISecret
    {
        public int DisposeCount { get; private set; }
        public int Length => throw new InvalidOperationException("length failed");
        public void CopyTo(Span<byte> destination) => throw new NotSupportedException();
        public ISecret Clone() => throw new NotSupportedException();
        public void Dispose() => DisposeCount++;
    }

    private sealed class CancelPrompt : ISshHostKeyPrompt
    {
        public ValueTask<SshHostKeyPromptDecision> PromptAsync(
            SshHostKeyPromptRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(SshHostKeyPromptDecision.Cancel);
    }

    private sealed class ThrowingSink : ISafeDiagnosticSink
    {
        public void Write(SafeDiagnosticEventInput diagnosticEvent) =>
            throw new InvalidOperationException("diagnostic sink failure");

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => [];
    }

    private sealed class BootstrapReasonErrorMapper : IErrorMapper
    {
        public WinArdError Map(Exception exception, ConnectionStage stage)
        {
            var code = exception is QualityBootstrapCompatibilityException compatibility
                ? $"BOOTSTRAP_{compatibility.Reason.ToString().ToUpperInvariant()}"
                : "OTHER";
            return WinArdError.Create(stage, code, "safe", Guid.NewGuid().ToString("N"));
        }
    }

    private sealed class RetryTransportFactory(List<string> events) : IRemoteTransportFactory
    {
        private int _count;

        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            var name = Interlocked.Increment(ref _count) == 1 ? "preferred" : "fallback";
            return Task.FromResult(new TransportConnection(
                new MemoryStream(),
                new EndPointDescription(profile.Host, profile.Port),
                new NamedLifetime(name, events)));
        }
    }

    private sealed class FailingTransportFactory(Exception failure) : IRemoteTransportFactory
    {
        public int Attempts { get; private set; }

        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromException<TransportConnection>(failure);
        }
    }

    private sealed class HostKeyBootstrapTransport(
        SshHostKeyCandidate candidate,
        List<string> events) : IRemoteTransportFactory
    {
        public int Attempts { get; private set; }

        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
            {
                events.Add("transport-hostkey");
                return Task.FromException<TransportConnection>(new SshHostKeyUnknownException(
                    SshHostKeyVerifier.Verify(candidate, null)));
            }

            var name = Attempts == 2 ? "preferred" : "fallback";
            events.Add($"transport-{name}");
            return Task.FromResult(new TransportConnection(
                new MemoryStream(),
                new EndPointDescription(profile.Host, profile.Port),
                new NamedLifetime(name, events)));
        }
    }

    private sealed class FallbackHostKeyTransport(
        SshHostKeyCandidate candidate,
        List<string> events) : IRemoteTransportFactory
    {
        public int Attempts { get; private set; }

        public Task<TransportConnection> ConnectAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 2)
            {
                events.Add("transport-fallback-hostkey");
                return Task.FromException<TransportConnection>(new SshHostKeyUnknownException(
                    SshHostKeyVerifier.Verify(candidate, null)));
            }

            var name = Attempts == 1 ? "preferred" : "fallback";
            events.Add($"transport-{name}");
            return Task.FromResult(new TransportConnection(
                new MemoryStream(),
                new EndPointDescription(profile.Host, profile.Port),
                new NamedLifetime(name, events)));
        }
    }

    private sealed class RetrySecretProvider(List<string> events) : IConnectionSecretProvider
    {
        private int _count;

        public ValueTask<ISecret> GetSecretAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            var name = Interlocked.Increment(ref _count) == 1 ? "preferred" : "fallback";
            return ValueTask.FromResult<ISecret>(new NamedSecret(name, events));
        }
    }

    private sealed class RetryClientFactory(Queue<RetryClient> clients, List<string> events) : IRfbClientFactory
    {
        public IRfbClient Create(Stream stream)
        {
            var client = clients.Dequeue();
            events.Add($"client-create-{client.Name}");
            return client;
        }
    }

    private sealed class RetryClient(
        string name,
        List<string> events,
        QualityBootstrapFailureReason? failure = null,
        Exception? bootstrapFailure = null,
        Exception? authenticationFailure = null,
        CancellationTokenSource? cancelBeforeBootstrap = null) : IRfbClient
    {
        private QualityBootstrapState _state = QualityBootstrapState.LegacyBgra32;
        public string Name => name;
        public QualityBootstrapState BootstrapState => _state;
        public ArdDisplayCapabilities QualityCapabilities => new(
            CapabilitySupport.Observed, CapabilitySupport.Observed,
            CapabilitySupport.Unknown, CapabilitySupport.Unknown, CapabilitySupport.Unknown,
            false, false, null);
        public Task NegotiateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AuthenticateAsync(string username, ISecret secret, CancellationToken cancellationToken) =>
            authenticationFailure is null
                ? Task.CompletedTask
                : Task.FromException(authenticationFailure);
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask ConfigureBootstrapAsync(
            QualityBootstrapSettings settings,
            QualityBootstrapAttempt attempt,
            CancellationToken cancellationToken)
        {
            events.Add($"{name}-configure-{attempt}-{settings.PixelFormat}");
            _state = new QualityBootstrapState(attempt, settings);
            return ValueTask.CompletedTask;
        }
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask<RemoteServerMessage> ReceiveBootstrapAsync(CancellationToken cancellationToken)
        {
            if (cancelBeforeBootstrap is not null)
            {
                cancelBeforeBootstrap.Cancel();
                return ValueTask.FromException<RemoteServerMessage>(
                    new OperationCanceledException(cancelBeforeBootstrap.Token));
            }

            return bootstrapFailure is not null
                ? ValueTask.FromException<RemoteServerMessage>(bootstrapFailure)
                : failure is { } reason
                ? ValueTask.FromException<RemoteServerMessage>(new QualityBootstrapCompatibilityException(reason))
                : ValueTask.FromResult<RemoteServerMessage>(new RemoteFramebufferMessage(
                    new RemoteFramebufferSize(1, 1), [0, 0, 0, 255], 4,
                    [new RemoteRectangle(0, 0, 1, 1)]));
        }
        public void ConfirmBootstrap(RemoteFramebufferSize framebufferSize)
        {
            _state = _state.ConfirmApplied();
        }
        public ValueTask DisposeAsync()
        {
            events.Add($"{name}-client-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NamedSecret(string name, List<string> events) : ISecret
    {
        public int Length => 1;
        public void CopyTo(Span<byte> destination) => destination[0] = 1;
        public ISecret Clone() => new NamedSecret(name, events);
        public void Dispose() => events.Add($"{name}-secret-dispose");
    }

    private sealed class NamedLifetime(string name, List<string> events) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            events.Add($"{name}-transport-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrustPrompt : ISshHostKeyPrompt
    {
        public int Count { get; private set; }

        public ValueTask<SshHostKeyPromptDecision> PromptAsync(
            SshHostKeyPromptRequest request,
            CancellationToken cancellationToken)
        {
            Count++;
            return ValueTask.FromResult(SshHostKeyPromptDecision.Trust);
        }
    }
}
