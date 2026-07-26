using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Diagnostics;
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
        AssertSecretOutput(output.ToArray());
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
    public async Task Truncated_client_isolated_then_valid_client_succeeds()
    {
        var broker = CreateBroker(
            new FakeCredentialStore(Encoding.UTF8.GetBytes(InjectedSecret)));
        await using var session = await broker.PrepareAsync(
            PasswordProfile(), CancellationToken.None);
        var environment = session.Configure(Start()).Environment!;
        await using (var malformed = await ConnectAsync(environment))
        {
            await malformed.WriteAsync(new byte[] { 1, 2, 3 });
        }

        using var output = new MemoryStream();
        Assert.Equal(
            0,
            await OpenSshAskPassClient.RunAsync(
                environment, output, CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_frame_is_rejected_then_valid_client_succeeds()
    {
        var broker = CreateBroker(
            new FakeCredentialStore(Encoding.UTF8.GetBytes(InjectedSecret)));
        await using var session = await broker.PrepareAsync(
            PasswordProfile(), CancellationToken.None);
        var environment = session.Configure(Start()).Environment!;
        await using (var malformed = await ConnectAsync(environment))
        {
            var length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(
                length,
                OpenSshAskPassProtocol.RequestBytes + 1);
            await malformed.WriteAsync(length);
            await malformed.FlushAsync();
        }

        using var output = new MemoryStream();
        Assert.Equal(
            0,
            await OpenSshAskPassClient.RunAsync(
                environment, output, CancellationToken.None));
    }

    [Fact]
    public async Task Authenticated_frame_does_not_probe_for_late_trailing_data()
    {
        var challenge = RandomNumberGenerator.GetBytes(OpenSshAskPassLimits.ChallengeBytes);
        var frame = new byte[sizeof(int) + OpenSshAskPassProtocol.RequestBytes];
        BinaryPrimitives.WriteInt32LittleEndian(frame, OpenSshAskPassProtocol.RequestBytes);
        OpenSshAskPassProtocol.Magic.CopyTo(frame.AsSpan(sizeof(int)));
        challenge.CopyTo(frame, sizeof(int) + OpenSshAskPassProtocol.Magic.Length);
        await using var stream = new ThrowOnReadPastEndStream(frame);

        Assert.True(
            await OpenSshAskPassSession.AuthenticateFrameAsync(
                stream,
                challenge,
                CancellationToken.None));
        Assert.Equal(frame.Length, stream.Position);
    }

    [Fact]
    public async Task Concurrent_clients_allow_exactly_one_secret_consumption()
    {
        var broker = CreateBroker(
            new FakeCredentialStore(Encoding.UTF8.GetBytes(InjectedSecret)));
        await using var session = await broker.PrepareAsync(
            PasswordProfile(), CancellationToken.None);
        var environment = session.Configure(Start()).Environment!;
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        var results = await Task.WhenAll(
            OpenSshAskPassClient.RunAsync(environment, first, CancellationToken.None),
            OpenSshAskPassClient.RunAsync(environment, second, CancellationToken.None));

        Assert.Equal(1, results.Count(static result => result == 0));
        Assert.Equal(1, results.Count(static result => result != 0));
    }

    [Fact]
    public async Task Stalled_client_times_out_without_consuming_the_session()
    {
        var broker = CreateBroker(
            new FakeCredentialStore(Encoding.UTF8.GetBytes(InjectedSecret)));
        await using var session = await broker.PrepareAsync(
            PasswordProfile(), CancellationToken.None);
        var environment = session.Configure(Start()).Environment!;
        await using var stalled = await ConnectAsync(environment);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        using var output = new MemoryStream();
        Assert.Equal(
            0,
            await OpenSshAskPassClient.RunAsync(
                environment, output, CancellationToken.None));
    }

    [Fact]
    public async Task Published_helper_process_returns_secret_and_safe_error_code()
    {
        var broker = CreateBroker(
            new FakeCredentialStore(Encoding.UTF8.GetBytes(InjectedSecret)));
        await using var session = await broker.PrepareAsync(
            PasswordProfile(), CancellationToken.None);
        var environment = session.Configure(Start()).Environment!;

        var success = await RunHelperAsync(environment);
        Assert.Equal(0, success.ExitCode);
        AssertSecretOutput(success.StandardOutput);
        Assert.Empty(success.StandardError);

        await using var errorSession = await broker.PrepareAsync(
            PasswordProfile(), CancellationToken.None);
        var invalid = new Dictionary<string, string>(
            errorSession.Configure(Start()).Environment!,
            StringComparer.OrdinalIgnoreCase)
        {
            [OpenSshAskPassEnvironment.Challenge] =
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };
        var failure = await RunHelperAsync(invalid);
        Assert.NotEqual(0, failure.ExitCode);
        Assert.Empty(failure.StandardOutput);
        Assert.StartsWith("ASKPASS_E_", failure.StandardError, StringComparison.Ordinal);
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

    private static async Task<NamedPipeClientStream> ConnectAsync(
        IReadOnlyDictionary<string, string> environment)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            environment[OpenSshAskPassEnvironment.PipeName],
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000);
        return pipe;
    }

    private static async Task<HelperResult> RunHelperAsync(
        IReadOnlyDictionary<string, string> environment)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WinARD.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var helper = Path.Combine(
            root!.FullName,
            "tools",
            "WinARD.OpenSshAskPass",
            "bin",
            configuration,
            "net8.0-windows10.0.19041.0",
            "WinARD.OpenSshAskPass.exe");
        var start = new ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var pair in environment)
        {
            start.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Unable to start askpass helper.");
        using var output = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await outputTask;
        return new HelperResult(process.ExitCode, output.ToArray(), await errorTask);
    }

    private sealed record HelperResult(
        int ExitCode,
        byte[] StandardOutput,
        string StandardError);

    private static void AssertSecretOutput(byte[] actual)
    {
        var expected = Encoding.UTF8.GetBytes(InjectedSecret + "\n");
        Span<byte> expectedDigest = stackalloc byte[32];
        Span<byte> actualDigest = stackalloc byte[32];
        try
        {
            SHA256.HashData(expected, expectedDigest);
            SHA256.HashData(actual, actualDigest);
            Assert.Equal(expected.Length, actual.Length);
            Assert.True(CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(expectedDigest);
            CryptographicOperations.ZeroMemory(actualDigest);
        }
    }

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

    private sealed class ThrowOnReadPastEndStream(byte[] contents) : MemoryStream(contents)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Position == Length)
            {
                throw new InvalidOperationException("Unexpected trailing-data probe.");
            }

            return base.ReadAsync(buffer, cancellationToken);
        }
    }

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
