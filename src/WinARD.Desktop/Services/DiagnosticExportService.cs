using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinARD.Infrastructure.Diagnostics;
using WinRT.Interop;

namespace WinARD.Desktop.Services;

public sealed class DiagnosticExportService(DiagnosticExporter exporter) : IDisposable
{
    private readonly DiagnosticExporter _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> ExportAsync(
        Window owner,
        DiagnosticExportContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"WinARD-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeChoices.Add("ZIP archive", [".zip"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
            var file = await picker.PickSaveFileAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (file is null)
            {
                return null;
            }

            await _exporter.ExportAsync(file.Path, context, cancellationToken).ConfigureAwait(false);
            return file.Path;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
