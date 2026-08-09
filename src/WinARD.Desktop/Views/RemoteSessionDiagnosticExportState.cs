namespace WinARD.Desktop.Views;

internal sealed class RemoteSessionDiagnosticExportState
{
    private readonly object _sync = new();
    private readonly bool _serviceAvailable;
    private bool _closing;
    private bool _exporting;
    private CancellationTokenSource? _activeExportCancellation;

    public RemoteSessionDiagnosticExportState(bool serviceAvailable) =>
        _serviceAvailable = serviceAvailable;

    public bool IsEnabled
    {
        get
        {
            lock (_sync)
            {
                return _serviceAvailable && !_closing && !_exporting;
            }
        }
    }

    public bool IsClosing
    {
        get
        {
            lock (_sync)
            {
                return _closing;
            }
        }
    }

    public bool TryBeginExport()
        => TryBeginExport(CancellationToken.None, out _);

    public bool TryBeginExport(
        CancellationToken lifetimeToken,
        out CancellationToken exportToken)
    {
        lock (_sync)
        {
            if (!_serviceAvailable || _closing || _exporting)
            {
                exportToken = default;
                return false;
            }

            _activeExportCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            _exporting = true;
            exportToken = _activeExportCancellation.Token;
            return true;
        }
    }

    public void CompleteExport()
    {
        lock (_sync)
        {
            _exporting = false;
            _activeExportCancellation?.Dispose();
            _activeExportCancellation = null;
        }
    }

    public void BeginClosing()
    {
        lock (_sync)
        {
            _closing = true;
            try
            {
                _activeExportCancellation?.Cancel();
            }
            catch (AggregateException)
            {
            }
        }
    }
}
