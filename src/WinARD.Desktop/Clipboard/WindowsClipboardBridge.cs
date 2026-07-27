using System.Security.Cryptography;
using Windows.ApplicationModel.DataTransfer;
using WinARD.Desktop.Threading;
using WinARD.Remote.Protocol.Clipboard;
using ClipboardApi = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace WinARD.Desktop.Clipboard;

internal interface IWindowsClipboardAdapter
{
    event EventHandler<object>? Changed;

    ValueTask<string?> GetTextAsync(CancellationToken cancellationToken);

    void SetText(string text);
}

public sealed class WindowsClipboardBridge : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IWindowsClipboardAdapter _adapter;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<string, CancellationToken, ValueTask> _sendRemote;
    private readonly int _maxUtf8Bytes;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RemoteClipboardSuppressionState _remoteSuppression = new();
    private Task _pending = Task.CompletedTask;
    private Task? _disposeTask;
    private long _generation;
    private bool _disposed;
    private bool _isEnabled = true;

    public WindowsClipboardBridge(
        IUiDispatcher dispatcher,
        Func<string, CancellationToken, ValueTask> sendRemote,
        int maxUtf8Bytes = ClipboardProtocol.DefaultMaxUtf8Bytes)
        : this(new WindowsClipboardAdapter(), dispatcher, sendRemote, maxUtf8Bytes)
    {
    }

    internal WindowsClipboardBridge(
        IWindowsClipboardAdapter adapter,
        IUiDispatcher dispatcher,
        Func<string, CancellationToken, ValueTask> sendRemote,
        int maxUtf8Bytes = ClipboardProtocol.DefaultMaxUtf8Bytes)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _sendRemote = sendRemote ?? throw new ArgumentNullException(nameof(sendRemote));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);
        _maxUtf8Bytes = maxUtf8Bytes;
        try
        {
            _dispatcher.InvokeAsync(
                () => _adapter.Changed += OnClipboardChanged,
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            _lifetime.Dispose();
            throw;
        }
    }

    public bool IsEnabled
    {
        get
        {
            lock (_sync)
            {
                return _isEnabled;
            }
        }
        set
        {
            lock (_sync)
            {
                if (_isEnabled == value)
                {
                    return;
                }

                _isEnabled = value;
                if (!value)
                {
                    _generation++;
                    ClearRemoteSuppressionLocked();
                }
            }
        }
    }

    internal int PendingRemoteSuppressionCount
    {
        get
        {
            lock (_sync)
            {
                return _remoteSuppression.PendingCount;
            }
        }
    }

    public async ValueTask SetRemoteTextAsync(string text, CancellationToken cancellationToken)
    {
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isEnabled)
            {
                return;
            }

            generation = _generation;
        }

        var sanitized = SanitizeAndDigest(text, _maxUtf8Bytes);
        try
        {
            var digestTransferred = false;
            await _dispatcher.InvokeAsync(
                () => digestTransferred = ApplyRemoteText(
                    sanitized.Text,
                    sanitized.Digest,
                    generation),
                cancellationToken).ConfigureAwait(false);
            if (digestTransferred)
            {
                sanitized = sanitized with { Digest = null! };
            }
        }
        finally
        {
            if (sanitized.Digest is not null)
            {
                CryptographicOperations.ZeroMemory(sanitized.Digest);
            }
        }
    }

    public Task WhenIdleAsync()
    {
        lock (_sync)
        {
            return _pending;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task pending;
        lock (_sync)
        {
            _disposed = true;
            _generation++;
            ClearRemoteSuppressionLocked();
            pending = _pending;
        }

        List<Exception> failures = [];
        CaptureFailure(_lifetime.Cancel, failures);
        await CaptureFailureAsync(
            () => _dispatcher.InvokeAsync(
                () => _adapter.Changed -= OnClipboardChanged,
                CancellationToken.None),
            failures).ConfigureAwait(false);

        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        CaptureFailure(_lifetime.Dispose, failures);
        if (failures.Count != 0)
        {
            throw new AggregateException("Windows clipboard bridge cleanup failed.", failures);
        }
    }

    private static void CaptureFailure(Action cleanup, List<Exception> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static async Task CaptureFailureAsync(Func<Task> cleanup, List<Exception> failures)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private void OnClipboardChanged(object? sender, object args)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var remoteWrite = _remoteSuppression.ClaimCallback();

            var generation = remoteWrite?.Generation ?? _generation;
            _pending = ObserveLocalChangeAsync(_pending, remoteWrite, generation, _lifetime.Token);
        }
    }

    private async Task ObserveLocalChangeAsync(
        Task predecessor,
        RemoteClipboardWriteContext? remoteWrite,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await predecessor.ConfigureAwait(false);
            if (remoteWrite is not null)
            {
                await remoteWrite.Completion.Task.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Task<string?>? readTask = null;
            await _dispatcher.InvokeAsync(
                () => readTask = _adapter.GetTextAsync(cancellationToken).AsTask(),
                cancellationToken).ConfigureAwait(false);
            var text = await (readTask ?? throw new InvalidOperationException("Clipboard read was not started."))
                .ConfigureAwait(false);
            if (text is null)
            {
                return;
            }

            SanitizedClipboardText sanitized;
            try
            {
                sanitized = SanitizeAndDigest(text, _maxUtf8Bytes);
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (WinARD.Remote.Protocol.Errors.RfbProtocolException)
            {
                return;
            }

            try
            {
                lock (_sync)
                {
                    if (_disposed || !_isEnabled || generation != _generation)
                    {
                        return;
                    }

                    if (remoteWrite is { Succeeded: true } &&
                        CryptographicOperations.FixedTimeEquals(
                            remoteWrite.Digest,
                            sanitized.Digest))
                    {
                        return;
                    }

                    ClearPendingRemoteSuppressionLocked();
                }

                await _sendRemote(sanitized.Text, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sanitized.Digest);
                remoteWrite?.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Clipboard contents are deliberately never included in diagnostics.
        }
    }

    private bool ApplyRemoteText(string text, byte[] digest, long generation)
    {
        RemoteClipboardWriteContext remoteWrite;
        lock (_sync)
        {
            if (_disposed || !_isEnabled || generation != _generation)
            {
                return false;
            }

            remoteWrite = new RemoteClipboardWriteContext(generation, digest);
            _remoteSuppression.Begin(remoteWrite);
        }

        try
        {
            _adapter.SetText(text);
            lock (_sync)
            {
                if (!_disposed && _isEnabled && generation == _generation)
                {
                    _remoteSuppression.PublishSuccessfulWrite(remoteWrite);
                }
            }
        }
        catch
        {
            remoteWrite.Dispose();
            throw;
        }
        finally
        {
            lock (_sync)
            {
                _remoteSuppression.CompleteWrite(remoteWrite);
            }

            remoteWrite.Completion.TrySetResult();
        }

        return true;
    }

    private void ClearRemoteSuppressionLocked()
    {
        _remoteSuppression.Clear();
    }

    private void ClearPendingRemoteSuppressionLocked()
    {
        _remoteSuppression.ClearPending();
    }

    private static SanitizedClipboardText SanitizeAndDigest(string text, int maxUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sanitized = new string(text.Where(character =>
            !char.IsControl(character) || character is '\t' or '\n' or '\r').ToArray());
        var encoded = ClipboardProtocol.EncodeText(sanitized, maxUtf8Bytes);
        try
        {
            return new SanitizedClipboardText(sanitized, SHA256.HashData(encoded));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private sealed record SanitizedClipboardText(string Text, byte[] Digest);

    private sealed class WindowsClipboardAdapter : IWindowsClipboardAdapter
    {
        public event EventHandler<object>? Changed
        {
            add => ClipboardApi.ContentChanged += value;
            remove => ClipboardApi.ContentChanged -= value;
        }

        public async ValueTask<string?> GetTextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = ClipboardApi.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return null;
            }

            return await content.GetTextAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }

        public void SetText(string text)
        {
            var package = new DataPackage();
            package.SetText(text);
            ClipboardApi.SetContent(package);
        }
    }
}

internal sealed class RemoteClipboardSuppressionState
{
    private RemoteClipboardWriteContext? _active;
    private RemoteClipboardWriteContext? _pending;

    public int PendingCount =>
        _active is null
            ? (_pending is null ? 0 : 1)
            : (_pending is null || ReferenceEquals(_active, _pending) ? 1 : 2);

    public void Begin(RemoteClipboardWriteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _active = context;
    }

    public void PublishSuccessfulWrite(RemoteClipboardWriteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Succeeded = true;
        if (context.CallbackClaimed)
        {
            return;
        }

        if (!ReferenceEquals(_pending, context))
        {
            _pending?.Dispose();
            _pending = context;
        }
    }

    public RemoteClipboardWriteContext? ClaimCallback()
    {
        if (_active is { } active)
        {
            active.CallbackClaimed = true;
            return active;
        }

        var pending = _pending;
        _pending = null;
        return pending;
    }

    public void CompleteWrite(RemoteClipboardWriteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(_active, context))
        {
            return;
        }

        if (context.CallbackClaimed && ReferenceEquals(_pending, context))
        {
            _pending = null;
        }

        _active = null;
    }

    public void ClearPending()
    {
        if (_pending is { } pending)
        {
            _pending = null;
            if (!ReferenceEquals(_active, pending))
            {
                pending.Dispose();
            }
        }
    }

    public void Clear()
    {
        var active = _active;
        var pending = _pending;
        _active = null;
        _pending = null;
        active?.Dispose();
        if (pending is not null && !ReferenceEquals(active, pending))
        {
            pending.Dispose();
        }
    }

    public bool References(RemoteClipboardWriteContext context) =>
        ReferenceEquals(_active, context) || ReferenceEquals(_pending, context);
}

internal sealed class RemoteClipboardWriteContext(long generation, byte[] digest) : IDisposable
{
    private byte[]? _digest = digest;

    public TaskCompletionSource Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public byte[] Digest => Volatile.Read(ref _digest) ??
        throw new ObjectDisposedException(nameof(RemoteClipboardWriteContext));

    public bool IsDisposed => Volatile.Read(ref _digest) is null;

    public long Generation { get; } = generation;

    public bool CallbackClaimed { get; set; }

    public bool Succeeded { get; set; }

    public void Dispose()
    {
        var digestToClear = Interlocked.Exchange(ref _digest, null);
        if (digestToClear is not null)
        {
            CryptographicOperations.ZeroMemory(digestToClear);
        }
    }
}
