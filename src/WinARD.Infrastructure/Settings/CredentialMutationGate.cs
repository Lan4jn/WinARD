using System.Security.Cryptography;
using System.Text;

namespace WinARD.Infrastructure.Settings;

public sealed class CredentialMutationGate : IDisposable
{
    private readonly Semaphore _semaphore;
    private int _disposed;

    public CredentialMutationGate(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _semaphore = new Semaphore(1, 1, NameFor(databasePath));
    }

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle? wait = null;
        wait = ThreadPool.RegisterWaitForSingleObject(
            _semaphore,
            static (state, _) =>
            {
                var (source, semaphore) = ((TaskCompletionSource, Semaphore))state!;
                if (!source.TrySetResult())
                {
                    semaphore.Release();
                }
            },
            (completion, _semaphore),
            Timeout.Infinite,
            executeOnlyOnce: true);
        using var cancellation = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(), completion);
        try
        {
            await completion.Task.ConfigureAwait(false);
            return new Lease(_semaphore);
        }
        finally
        {
            wait.Unregister(null);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }

    private static string NameFor(string databasePath)
    {
        var normalized = Path.GetFullPath(databasePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return $"Local\\WinARD.CredentialMutation.{hash}";
    }

    private sealed class Lease(Semaphore semaphore) : IDisposable
    {
        private Semaphore? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
