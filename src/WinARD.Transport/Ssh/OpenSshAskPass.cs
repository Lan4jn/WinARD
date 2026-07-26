using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using WinARD.Application.Ports;
using WinARD.Domain.Connections;
using WinARD.Domain.Security;

namespace WinARD.Transport.Ssh;

public static class OpenSshAskPassEnvironment
{
    public const string PipeName = "WINARD_ASKPASS_PIPE";
    public const string Challenge = "WINARD_ASKPASS_CHALLENGE";
    public const string TimeoutMilliseconds = "WINARD_ASKPASS_TIMEOUT_MS";
}

public static class OpenSshAskPassLimits
{
    public const int ChallengeBytes = 32;
    public const int MaximumSecretBytes = 4096;
    public const int MaximumAttempts = 2;
}

public sealed class OpenSshAskPassException : InvalidOperationException
{
    public OpenSshAskPassException(string safeMessage)
        : base(safeMessage)
    {
    }
}

public interface IOpenSshAskPassSession : IAsyncDisposable
{
    OpenSshProcessStart Configure(OpenSshProcessStart start);
}

internal sealed class NoOpenSshAskPassSession : IOpenSshAskPassSession
{
    public static NoOpenSshAskPassSession Instance { get; } = new();

    public OpenSshProcessStart Configure(OpenSshProcessStart start) =>
        start ?? throw new ArgumentNullException(nameof(start));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class OpenSshAskPassBroker : IOpenSshAskPassBroker
{
    private readonly ICredentialStore _credentialStore;
    private readonly string _helperPath;
    private readonly TimeSpan _timeout;

    public OpenSshAskPassBroker(ICredentialStore credentialStore)
        : this(
            credentialStore,
            TrustedOpenSshAskPassHelper.Resolve(),
            TimeSpan.FromSeconds(30),
            requireExistingHelper: true)
    {
    }

    internal OpenSshAskPassBroker(
        ICredentialStore credentialStore,
        string helperPath,
        TimeSpan timeout,
        bool requireExistingHelper)
    {
        _credentialStore = credentialStore ??
            throw new ArgumentNullException(nameof(credentialStore));
        _helperPath = TrustedOpenSshAskPassHelper.Validate(
            helperPath,
            requireExistingHelper);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _timeout = timeout;
    }

    public ValueTask EnsureSupportedAsync(
        SshProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IOpenSshAskPassSession> PrepareAsync(
        SshProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var reference = SelectReference(profile);
        if (reference is null)
        {
            return NoOpenSshAskPassSession.Instance;
        }

        var promptPurpose = profile.PrivateKeyPath is null
            ? CredentialPromptPurpose.SshPassword
            : CredentialPromptPurpose.PrivateKeyPassphrase;
        var secret = _credentialStore is IPurposeAwareCredentialStore purposeAwareStore
            ? await purposeAwareStore.ReadForPromptAsync(
                new CredentialPromptRequest(reference, promptPurpose),
                cancellationToken).ConfigureAwait(false)
            : string.Equals(reference.Store, "ask", StringComparison.OrdinalIgnoreCase)
                ? throw new OpenSshAskPassException(
                    "The configured credential store cannot prompt for SSH credentials.")
                : await _credentialStore.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            throw new OpenSshAskPassException(
                "The requested SSH credential was not found.");
        }

        try
        {
            ValidateSecret(secret);
            var purpose = profile.PrivateKeyPath is null
                ? OpenSshAskPassPurpose.Password
                : OpenSshAskPassPurpose.PrivateKeyPassphrase;
            var session = new OpenSshAskPassSession(
                secret,
                _helperPath,
                _timeout,
                purpose);
            secret = null;
            return session;
        }
        finally
        {
            secret?.Dispose();
        }
    }

    private static CredentialReference? SelectReference(SshProfile profile) =>
        profile.PrivateKeyPath is null
            ? profile.PasswordCredentialReference
            : profile.PrivateKeyPassphraseCredentialReference;

    private static void ValidateSecret(ISecret secret)
    {
        if (secret.Length is < 1 or > OpenSshAskPassLimits.MaximumSecretBytes)
        {
            throw new OpenSshAskPassException(
                "The SSH credential has an unsupported length.");
        }

        var bytes = new byte[secret.Length];
        try
        {
            secret.CopyTo(bytes);
            if (bytes.AsSpan().IndexOfAny((byte)0, (byte)'\r', (byte)'\n') >= 0)
            {
                throw new OpenSshAskPassException(
                    "The SSH credential contains unsupported bytes.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

internal enum OpenSshAskPassPurpose
{
    Password,
    PrivateKeyPassphrase,
}

internal sealed class OpenSshAskPassSession : IOpenSshAskPassSession
{
    private readonly ISecret _secret;
    private readonly string _helperPath;
    private readonly TimeSpan _timeout;
    private readonly string _pipeName = $"WinARD.AskPass.{Guid.NewGuid():N}";
    private readonly byte[] _challenge =
        RandomNumberGenerator.GetBytes(OpenSshAskPassLimits.ChallengeBytes);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeSync = new();
    private readonly Task _serverTask;
    private Task? _disposeTask;
    private int _configured;
    private int _consumed;
    private int _secretDisposed;

    public OpenSshAskPassSession(
        ISecret secret,
        string helperPath,
        TimeSpan timeout,
        OpenSshAskPassPurpose purpose)
    {
        _secret = secret;
        _helperPath = helperPath;
        _timeout = timeout;
        Purpose = purpose;
        _serverTask = ServeAsync();
    }

    public OpenSshAskPassPurpose Purpose { get; }

    public OpenSshProcessStart Configure(OpenSshProcessStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (Interlocked.Exchange(ref _configured, 1) != 0)
        {
            throw new OpenSshAskPassException(
                "The SSH askpass session was already configured.");
        }

        var environment = start.Environment is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(
                start.Environment,
                StringComparer.OrdinalIgnoreCase);
        environment["SSH_ASKPASS"] = _helperPath;
        environment["SSH_ASKPASS_REQUIRE"] = "force";
        environment["DISPLAY"] = "WinARD";
        environment[OpenSshAskPassEnvironment.PipeName] = _pipeName;
        environment[OpenSshAskPassEnvironment.Challenge] =
            Convert.ToBase64String(_challenge);
        environment[OpenSshAskPassEnvironment.TimeoutMilliseconds] =
            Math.Clamp((long)_timeout.TotalMilliseconds, 1, int.MaxValue)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return start with { Environment = environment };
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            task = _disposeTask;
        }

        return new ValueTask(task);
    }

    private async Task ServeAsync()
    {
        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            timeout.Token);
        try
        {
            for (var attempt = 0;
                 attempt < OpenSshAskPassLimits.MaximumAttempts &&
                 Volatile.Read(ref _consumed) == 0;
                 attempt++)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
                    using var clientTimeout = new CancellationTokenSource(
                        TimeSpan.FromMilliseconds(
                            Math.Clamp(_timeout.TotalMilliseconds / 2, 100, 1000)));
                    using var clientLinked = CancellationTokenSource.CreateLinkedTokenSource(
                        linked.Token,
                        clientTimeout.Token);
                    if (!await AuthenticateAsync(pipe, clientLinked.Token).ConfigureAwait(false))
                    {
                        await WriteStatusAsync(
                            pipe,
                            OpenSshAskPassStatus.Unauthorized,
                            clientLinked.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (Interlocked.Exchange(ref _consumed, 1) != 0)
                    {
                        await WriteStatusAsync(
                            pipe,
                            OpenSshAskPassStatus.Consumed,
                            clientLinked.Token).ConfigureAwait(false);
                        continue;
                    }

                    await WriteSecretAsync(pipe, clientLinked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!linked.IsCancellationRequested)
                {
                }
                catch (EndOfStreamException)
                {
                }
                catch (IOException)
                {
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            DisposeSecret();
        }
    }

    private async Task<bool> AuthenticateAsync(
        Stream pipe,
        CancellationToken cancellationToken) =>
        await AuthenticateFrameAsync(pipe, _challenge, cancellationToken).ConfigureAwait(false);

    internal static async Task<bool> AuthenticateFrameAsync(
        Stream pipe,
        ReadOnlyMemory<byte> challenge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (challenge.Length != OpenSshAskPassLimits.ChallengeBytes)
        {
            return false;
        }

        var lengthBytes = new byte[sizeof(int)];
        var request = new byte[OpenSshAskPassProtocol.RequestBytes];
        try
        {
            await ReadExactlyAsync(pipe, lengthBytes, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadInt32LittleEndian(lengthBytes) != request.Length)
            {
                return false;
            }

            await ReadExactlyAsync(pipe, request, cancellationToken).ConfigureAwait(false);
            var authenticated = request.AsSpan(0, OpenSshAskPassProtocol.Magic.Length)
                    .SequenceEqual(OpenSshAskPassProtocol.Magic) &&
                CryptographicOperations.FixedTimeEquals(
                    request.AsSpan(OpenSshAskPassProtocol.Magic.Length),
                    challenge.Span);
            return authenticated;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(lengthBytes);
            CryptographicOperations.ZeroMemory(request);
        }
    }

    private async Task WriteSecretAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        var secret = new byte[_secret.Length];
        var length = new byte[sizeof(int)];
        try
        {
            _secret.CopyTo(secret);
            BinaryPrimitives.WriteInt32LittleEndian(length, secret.Length);
            await WriteStatusAsync(
                pipe,
                OpenSshAskPassStatus.Success,
                cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(length, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(secret, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(length);
        }
    }

    private static async Task WriteStatusAsync(
        Stream pipe,
        OpenSshAskPassStatus status,
        CancellationToken cancellationToken)
    {
        var value = new[] { (byte)status };
        await pipe.WriteAsync(value, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        finally
        {
            DisposeSecret();
            CryptographicOperations.ZeroMemory(_challenge);
            _lifetime.Dispose();
        }
    }

    private void DisposeSecret()
    {
        if (Interlocked.Exchange(ref _secretDisposed, 1) == 0)
        {
            _secret.Dispose();
        }
    }

    internal static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream
                .ReadAsync(destination[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }
}

internal enum OpenSshAskPassStatus : byte
{
    Success = 0,
    Unauthorized = 1,
    Consumed = 2,
    Invalid = 3,
}

internal static class OpenSshAskPassProtocol
{
    public static ReadOnlySpan<byte> Magic => "WAP1"u8;

    public const int RequestBytes = 4 + OpenSshAskPassLimits.ChallengeBytes;
}

public static class OpenSshAskPassClient
{
    public static async Task<int> RunAsync(
        IReadOnlyDictionary<string, string> environment,
        Stream standardOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(standardOutput);
        try
        {
            var pipeName = Required(environment, OpenSshAskPassEnvironment.PipeName);
            var challengeText = Required(
                environment,
                OpenSshAskPassEnvironment.Challenge);
            var challenge = Convert.FromBase64String(challengeText);
            try
            {
                if (challenge.Length != OpenSshAskPassLimits.ChallengeBytes)
                {
                    return 11;
                }

                var timeoutMilliseconds = int.Parse(
                    Required(
                        environment,
                        OpenSshAskPassEnvironment.TimeoutMilliseconds),
                    System.Globalization.CultureInfo.InvariantCulture);
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(Math.Clamp(timeoutMilliseconds, 1, 60_000)));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
                var request = new byte[
                    sizeof(int) + OpenSshAskPassProtocol.RequestBytes];
                try
                {
                    BinaryPrimitives.WriteInt32LittleEndian(
                        request,
                        OpenSshAskPassProtocol.RequestBytes);
                    OpenSshAskPassProtocol.Magic.CopyTo(
                        request.AsSpan(sizeof(int)));
                    challenge.CopyTo(
                        request,
                        sizeof(int) + OpenSshAskPassProtocol.Magic.Length);
                    await pipe.WriteAsync(request, linked.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(request);
                }

                var status = new byte[1];
                await OpenSshAskPassSession.ReadExactlyAsync(
                    pipe,
                    status,
                    linked.Token).ConfigureAwait(false);
                if (status[0] != (byte)OpenSshAskPassStatus.Success)
                {
                    return 12;
                }

                var lengthBytes = new byte[sizeof(int)];
                await OpenSshAskPassSession.ReadExactlyAsync(
                    pipe,
                    lengthBytes,
                    linked.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                CryptographicOperations.ZeroMemory(lengthBytes);
                if (length is < 1 or > OpenSshAskPassLimits.MaximumSecretBytes)
                {
                    return 13;
                }

                var secret = new byte[length];
                try
                {
                    await OpenSshAskPassSession.ReadExactlyAsync(
                        pipe,
                        secret,
                        linked.Token).ConfigureAwait(false);
                    await standardOutput
                        .WriteAsync(secret, cancellationToken)
                        .ConfigureAwait(false);
                    await standardOutput
                        .WriteAsync(new byte[] { (byte)'\n' }, cancellationToken)
                        .ConfigureAwait(false);
                    await standardOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return 0;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(challenge);
            }
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            return 10;
        }
    }

    private static string Required(
        IReadOnlyDictionary<string, string> environment,
        string name)
    {
        if (!environment.TryGetValue(name, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new OpenSshAskPassException(
                "The SSH askpass helper environment is incomplete.");
        }

        return value;
    }
}

internal static class TrustedOpenSshAskPassHelper
{
    private const string FileName = "WinARD.OpenSshAskPass.exe";

    public static string Resolve() =>
        Validate(
            Path.Combine(AppContext.BaseDirectory, FileName),
            requireExisting: true);

    public static string Validate(string path, bool requireExisting)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            !string.Equals(
                Path.GetFileName(path),
                FileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenSshAskPassException(
                "The SSH askpass helper path is not trusted.");
        }

        var fullPath = Path.GetFullPath(path);
        var expectedDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (!string.Equals(
            Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar),
            expectedDirectory.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenSshAskPassException(
                "The SSH askpass helper must be in the application directory.");
        }

        if (requireExisting && !File.Exists(fullPath))
        {
            throw new OpenSshAskPassException(
                "The SSH askpass helper is missing.");
        }

        return fullPath;
    }
}
