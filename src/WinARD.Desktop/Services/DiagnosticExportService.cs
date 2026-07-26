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
    private readonly SemaphoreSlim _gate = new(1, 1);

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
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
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
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
