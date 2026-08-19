using System.Security.Cryptography;
using System.Text;

namespace WinARD.Infrastructure.Settings;

public sealed class CredentialMutationGate : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Func<CancellationToken, ValueTask>? _afterOpen;
    private int _disposed;

    public CredentialMutationGate(string databasePath) : this(databasePath, afterOpen: null)
    {
    }

    internal CredentialMutationGate(
        string databasePath,
        Func<CancellationToken, ValueTask>? afterOpen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        LockFilePath = PathFor(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(LockFilePath)!);
        _afterOpen = afterOpen;
    }

    internal string LockFilePath { get; }

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposeCancellation.Token);
        while (true)
        {
            waitCancellation.Token.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    LockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                try
                {
                    if (_afterOpen is not null)
                    {
                        await _afterOpen(cancellationToken).ConfigureAwait(false);
                    }
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    return new Lease(stream);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                ObjectDisposedException.ThrowIf(
                    _disposeCancellation.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested,
                    this);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await Task.Delay(RetryDelay, waitCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    _disposeCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    ObjectDisposedException.ThrowIf(
                        _disposeCancellation.IsCancellationRequested,
                        this);
                    throw;
                }
            }
            catch (OperationCanceledException) when (
                _disposeCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                ObjectDisposedException.ThrowIf(
                    _disposeCancellation.IsCancellationRequested,
                    this);
                throw;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _disposeCancellation.Cancel();
            _disposeCancellation.Dispose();
        }
    }

    private static string PathFor(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = fullPath.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return Path.Combine(
            Path.GetDirectoryName(fullPath)!,
            $".{Path.GetFileName(fullPath).ToLowerInvariant()}.{hash}.credential.lock");
    }

    private static bool IsLockContention(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private sealed class Lease(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}
