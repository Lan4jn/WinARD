using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinARD.Infrastructure.Diagnostics;
using WinRT.Interop;

namespace WinARD.Desktop.Services;

public interface IDiagnosticSavePicker
{
    Task<string?> PickPathAsync(Window? owner, CancellationToken cancellationToken);
}

public sealed class WindowsDiagnosticSavePicker : IDiagnosticSavePicker
{
    public async Task<string?> PickPathAsync(Window? owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var picker = new FileSavePicker
        {
            SuggestedFileName = $"WinARD-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}",
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("ZIP archive", [".zip"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        var file = await picker.PickSaveFileAsync().AsTask(cancellationToken).ConfigureAwait(false);
        return file?.Path;
    }
}

public sealed class DiagnosticExportService : IDisposable
{
    private readonly DiagnosticExporter _exporter;
    private readonly IDiagnosticSavePicker _picker;
    private readonly object _gate = new();
    private bool _busy;
    private bool _disposed;

    public DiagnosticExportService(
        DiagnosticExporter exporter,
        IDiagnosticSavePicker? picker = null)
    {
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _picker = picker ?? new WindowsDiagnosticSavePicker();
    }

    public Task<string?> ExportAsync(
        Window owner,
        DiagnosticExportContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return ExportCoreAsync(owner, context, cancellationToken);
    }

    internal Task<string?> ExportForTestAsync(
        DiagnosticExportContext context,
        CancellationToken cancellationToken) => ExportCoreAsync(null, context, cancellationToken);

    private async Task<string?> ExportCoreAsync(
        Window? owner,
        DiagnosticExportContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryEnterExport(cancellationToken))
        {
            return null;
        }

        try
        {
            var path = await _picker.PickPathAsync(owner, cancellationToken).ConfigureAwait(false);
            if (path is null)
            {
                return null;
            }

            await _exporter.ExportAsync(path, context, cancellationToken).ConfigureAwait(false);
            return path;
        }
        finally
        {
            ExitExport();
        }
    }

    private bool TryEnterExport(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_busy)
            {
                return false;
            }

            _busy = true;
            return true;
        }
    }

    private void ExitExport()
    {
        lock (_gate)
        {
            _busy = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }
}
