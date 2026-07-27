using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.Services;
using WinARD.Domain.Connections;
using WinARD.Domain.Sessions;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Transport.Ssh;
using Xunit;

namespace WinARD.Desktop.Tests.Services;

public sealed class ConnectionFailureDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"winard-failure-diagnostics-{Guid.NewGuid():N}");

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
}
