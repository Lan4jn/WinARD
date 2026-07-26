using Microsoft.UI.Xaml;
using WinARD.Desktop.Services;
using WinARD.Infrastructure.Diagnostics;
using Xunit;

namespace WinARD.Desktop.Tests.Services;

public sealed class DiagnosticExportServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"winard-picker-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CancelReturnsNullWithoutCreatingArchive()
    {
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new InMemorySafeDiagnosticSink(redactor), redactor);
        using var service = new DiagnosticExportService(exporter, new Picker(_ => Task.FromResult<string?>(null)));

        var result = await service.ExportForTestAsync(DiagnosticExportContext.Empty, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SuccessfulPickExportsArchive()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "picked.zip");
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new InMemorySafeDiagnosticSink(redactor), redactor);
        using var service = new DiagnosticExportService(exporter, new Picker(_ => Task.FromResult<string?>(path)));

        var result = await service.ExportForTestAsync(DiagnosticExportContext.Empty, CancellationToken.None);

        Assert.Equal(path, result);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task PickerAndExporterFailuresPropagateAndReleaseBusyGate()
    {
        Directory.CreateDirectory(_directory);
        var valid = Path.Combine(_directory, "valid.zip");
        var picker = new QueuePicker(
            _ => Task.FromException<string?>(new InvalidOperationException("picker failed")),
            _ => Task.FromResult<string?>(Path.Combine(_directory, "bad\0name.zip")),
            _ => Task.FromResult<string?>(valid));
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new InMemorySafeDiagnosticSink(redactor), redactor);
        using var service = new DiagnosticExportService(exporter, picker);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportForTestAsync(DiagnosticExportContext.Empty, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            service.ExportForTestAsync(DiagnosticExportContext.Empty, CancellationToken.None));
        Assert.Equal(valid, await service.ExportForTestAsync(DiagnosticExportContext.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentExportReturnsNullWhilePickerIsBusy()
    {
        var picker = new BlockingPicker();
        using var redactor = new SecretRedactor();
        using var exporter = new DiagnosticExporter(new InMemorySafeDiagnosticSink(redactor), redactor);
        using var service = new DiagnosticExportService(exporter, picker);

        var first = service.ExportForTestAsync(DiagnosticExportContext.Empty, CancellationToken.None);
        await picker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = await service.ExportForTestAsync(DiagnosticExportContext.Empty, CancellationToken.None);
        picker.Release.TrySetResult(null);

        Assert.Null(second);
        Assert.Null(await first);
        Assert.Equal(1, picker.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class Picker(Func<CancellationToken, Task<string?>> pick) : IDiagnosticSavePicker
    {
        public Task<string?> PickPathAsync(Window? owner, CancellationToken cancellationToken) => pick(cancellationToken);
    }

    private sealed class QueuePicker(params Func<CancellationToken, Task<string?>>[] picks) : IDiagnosticSavePicker
    {
        private readonly Queue<Func<CancellationToken, Task<string?>>> _picks = new(picks);

        public Task<string?> PickPathAsync(Window? owner, CancellationToken cancellationToken) =>
            _picks.Dequeue()(cancellationToken);
    }

    private sealed class BlockingPicker : IDiagnosticSavePicker
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public Task<string?> PickPathAsync(Window? owner, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult();
            return Release.Task;
        }
    }
}
