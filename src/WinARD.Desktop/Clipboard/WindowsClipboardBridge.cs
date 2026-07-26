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
    private Task _pending = Task.CompletedTask;
    private Task? _disposeTask;
    private string? _suppressedText;
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
                _isEnabled = value;
                if (!value)
                {
                    _suppressedText = null;
                }
            }
        }
    }

    public async ValueTask SetRemoteTextAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsEnabled)
        {
            return;
        }
        var sanitized = Sanitize(text, _maxUtf8Bytes);
        lock (_sync)
        {
            _suppressedText = sanitized;
        }

        await _dispatcher.InvokeAsync(() => _adapter.SetText(sanitized), cancellationToken)
            .ConfigureAwait(false);
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

            _pending = ObserveLocalChangeAsync(_pending, _lifetime.Token);
        }
    }

    private async Task ObserveLocalChangeAsync(Task predecessor, CancellationToken cancellationToken)
    {
        try
        {
            await predecessor.ConfigureAwait(false);
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

            string sanitized;
            try
            {
                sanitized = Sanitize(text, _maxUtf8Bytes);
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (WinARD.Remote.Protocol.Errors.RfbProtocolException)
            {
                return;
            }

            lock (_sync)
            {
                if (_disposed || !_isEnabled)
                {
                    return;
                }

                if (string.Equals(_suppressedText, sanitized, StringComparison.Ordinal))
                {
                    _suppressedText = null;
                    return;
                }
            }

            await _sendRemote(sanitized, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Clipboard contents are deliberately never included in diagnostics.
        }
    }

    private static string Sanitize(string text, int maxUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sanitized = new string(text.Where(character =>
            !char.IsControl(character) || character is '\t' or '\n' or '\r').ToArray());
        var encoded = ClipboardProtocol.EncodeText(sanitized, maxUtf8Bytes);
        CryptographicOperations.ZeroMemory(encoded);
        return sanitized;
    }

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
