namespace WinARD.Desktop.Views;

internal sealed class RemoteSessionDiagnosticExportState
{
    private readonly object _sync = new();
    private readonly bool _serviceAvailable;
    private bool _closing;
    private bool _exporting;

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

    public bool TryBeginExport()
    {
        lock (_sync)
        {
            if (!_serviceAvailable || _closing || _exporting)
            {
                return false;
            }

            _exporting = true;
            return true;
        }
    }

    public void CompleteExport()
    {
        lock (_sync)
        {
            _exporting = false;
        }
    }

    public void BeginClosing()
    {
        lock (_sync)
        {
            _closing = true;
        }
    }
}
