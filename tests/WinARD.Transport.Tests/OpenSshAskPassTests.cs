using System.Security.Cryptography;
using System.Text;
using WinARD.Application.Ports;
using WinARD.Domain.Connections;
using WinARD.Domain.Security;
using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Transport.Tests;

public sealed class OpenSshAskPassTests
{
    private const string InjectedSecret = "askpass-value-77c9";

    [Fact]
    public async Task Successful_exchange_keeps_secret_out_of_arguments_and_environment()
    {
        var store = new FakeCredentialStore(Encoding.UTF8.GetBytes(InjectedSecret));
        var broker = CreateBroker(store);
        await using var session = await broker.PrepareAsync(
            PasswordProfile(),
            CancellationToken.None);
        var start = session.Configure(
            new OpenSshProcessStart(
                Path.GetFullPath(@"C:\Windows\System32\OpenSSH\ssh.exe"),
                ["-T", "user@host"]));

        Assert.DoesNotContain(
            start.Arguments,
            value => value.Contains(InjectedSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(
            start.Environment!,
            pair => pair.Value.Contains(InjectedSecret, StringComparison.Ordinal));
        Assert.Equal("force", start.Environment!["SSH_ASKPASS_REQUIRE"]);
        Assert.True(Path.IsPathFullyQualified(start.Environment["SSH_ASKPASS"]));

        using var output = new MemoryStream();
        var exitCode = await OpenSshAskPassClient.RunAsync(
            start.Environment,
            output,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [.. Encoding.UTF8.GetBytes(InjectedSecret), (byte)'\n'],
            output.ToArray());
    }

    [Fact]
    public async Task Wrong_challenge_is_rejected_without_revealing_secret()
    {
        var broker = CreateBroker(
            new FakeCredentialStore(Encoding.UTF8.GetBytes(InjectedSecret)));
        await using var session = await broker.PrepareAsync(
            PasswordProfile(),
            CancellationToken.None);
        var start = session.Configure(Start());
        var environment = new Dictionary<string, string>(
            start.Environment!,
            StringComparer.OrdinalIgnoreCase)
        {
            [OpenSshAskPassEnvironment.Challenge] =
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };
        using var output = new MemoryStream();

        var exitCode = await OpenSshAskPassClient.RunAsync(
            environment,
            output,
            CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task Successful_challenge_is_single_use_and_replay_fails()
    {
        var broker = CreateBroker(
            new FakeCredentialStore(Encoding.UTF8.GetBytes(InjectedSecret)));
        await using var session = await broker.PrepareAsync(
            PasswordProfile(),
            CancellationToken.None);
        var start = session.Configure(Start());
        using var first = new MemoryStream();
        using var replay = new MemoryStream();

        Assert.Equal(
            0,
            await OpenSshAskPassClient.RunAsync(
                start.Environment!,
                first,
                CancellationToken.None));
        Assert.NotEqual(
            0,
            await OpenSshAskPassClient.RunAsync(
                start.Environment!,
                replay,
                CancellationToken.None));
        Assert.Empty(replay.ToArray());
    }

    [Fact]
    public async Task Oversized_secret_is_rejected_and_cancelled_session_disposes_secret()
    {
        var store = new FakeCredentialStore(new byte[OpenSshAskPassLimits.MaximumSecretBytes + 1]);
        var broker = CreateBroker(store);

        await Assert.ThrowsAsync<OpenSshAskPassException>(
            () => broker.PrepareAsync(
                PasswordProfile(),
                CancellationToken.None).AsTask());

        Assert.True(store.LastReadWasDisposed);
    }

    [Fact]
    public async Task Session_without_a_helper_can_be_cancelled_and_disposed_promptly()
    {
        var store = new FakeCredentialStore(Encoding.UTF8.GetBytes(InjectedSecret));
        var broker = CreateBroker(store);
        var session = await broker.PrepareAsync(
            PasswordProfile(),
            CancellationToken.None);
        session.Configure(Start());

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(store.LastReadWasDisposed);
    }

    private static OpenSshAskPassBroker CreateBroker(ICredentialStore store) =>
        new(
            store,
            Path.Combine(AppContext.BaseDirectory, "WinARD.OpenSshAskPass.exe"),
            TimeSpan.FromSeconds(2),
            requireExistingHelper: false);

    private static OpenSshProcessStart Start() =>
        new(
            Path.GetFullPath(@"C:\Windows\System32\OpenSSH\ssh.exe"),
            ["-T", "user@host"]);

    private static SshProfile PasswordProfile() =>
        SshProfile.Create(
            "host",
            22,
            "user",
            privateKeyPath: null,
            targetHost: "127.0.0.1",
            targetPort: 5900,
            CredentialReference.Create("windows", "ssh-password"),
            pinnedHostKeyAlgorithm: null,
            pinnedHostKeySha256: null);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1001:Types that own disposable fields should be disposable",
        Justification = "The broker takes ownership of each secret returned by the fake.")]
    private sealed class FakeCredentialStore(byte[] value) : ICredentialStore
    {
        private TrackingSecret? _last;

        public bool LastReadWasDisposed => _last?.Disposed == true;

        public ValueTask SaveAsync(
            CredentialReference reference,
            ISecret secret,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ISecret?> ReadAsync(
            CredentialReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _last = new TrackingSecret(value);
            return ValueTask.FromResult<ISecret?>(_last);
        }

        public ValueTask DeleteAsync(
            CredentialReference reference,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TrackingSecret(byte[] value) : ISecret
    {
        private byte[]? _value = value.ToArray();

        public bool Disposed { get; private set; }

        public int Length => _value?.Length ??
            throw new ObjectDisposedException(nameof(TrackingSecret));

        public void CopyTo(Span<byte> destination)
        {
            (_value ?? throw new ObjectDisposedException(nameof(TrackingSecret)))
                .CopyTo(destination);
        }

        public ISecret Clone() =>
            new TrackingSecret(
                _value ?? throw new ObjectDisposedException(nameof(TrackingSecret)));

        public void Dispose()
        {
            if (_value is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_value);
            _value = null;
            Disposed = true;
        }
    }
}
