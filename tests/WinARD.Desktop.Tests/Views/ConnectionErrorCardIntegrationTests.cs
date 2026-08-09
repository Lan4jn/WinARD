using Xunit;

namespace WinARD.Desktop.Tests.Views;

public sealed class ConnectionErrorCardIntegrationTests
{
    [Fact]
    public void CardExposesStableAutomationSurface()
    {
        var xaml = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "ConnectionErrorCard.xaml"));

        Assert.Contains("ConnectionErrorCard", xaml, StringComparison.Ordinal);
        Assert.Contains("ConnectionErrorTitle", xaml, StringComparison.Ordinal);
        Assert.Contains("ConnectionErrorSummary", xaml, StringComparison.Ordinal);
        Assert.Contains("ConnectionErrorCorrelationId", xaml, StringComparison.Ordinal);
        Assert.Contains("ConnectionErrorActions", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowAndEditorHostTheActionableCard()
    {
        var main = File.ReadAllText(RepositoryFile("src", "WinARD.Desktop", "MainWindow.xaml.cs"));
        var editor = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "ConnectionEditorDialog.xaml"));

        Assert.Contains("ConnectionErrorCard", main, StringComparison.Ordinal);
        Assert.Contains("ConnectionErrorCard", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionErrorSmokeExercisesInterruptedSessionRetryAndDisconnectActions()
    {
        var app = File.ReadAllText(RepositoryFile("src", "WinARD.Desktop", "App.xaml.cs"));

        Assert.Contains("--connection-error-smoke", app, StringComparison.Ordinal);
        Assert.Contains("REMOTE_SESSION_INTERRUPTED", app, StringComparison.Ordinal);
        Assert.Contains("isConnectionErrorSmoke", app, StringComparison.Ordinal);
        Assert.Contains("isPresenterFailureSmoke", app, StringComparison.Ordinal);
        Assert.Contains("retryRequested:", app, StringComparison.Ordinal);
    }

    [Fact]
    public void InputFailureSmokeUsesRealPointerFailureAndTheWindowActionHandler()
    {
        var app = File.ReadAllText(RepositoryFile("src", "WinARD.Desktop", "App.xaml.cs"));
        var window = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

        Assert.Contains("--remote-session-smoke-input-failure", app, StringComparison.Ordinal);
        Assert.Contains("failPointerSend: isInputFailureSmoke", app, StringComparison.Ordinal);
        Assert.Contains("WINARD_REMOTE_SMOKE_ERROR_MARKER", window, StringComparison.Ordinal);
        Assert.Contains("WINARD_REMOTE_SMOKE_ERROR_ACTION", window, StringComparison.Ordinal);
        Assert.Contains(
            "_errorActionHandler.HandleAsync(action, CancellationToken.None)",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticPickerIsInitializedWithTheOwningWindow()
    {
        var service = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Services", "DiagnosticExportService.cs"));

        Assert.Contains("FileSavePicker", service, StringComparison.Ordinal);
        Assert.Contains("WindowNative.GetWindowHandle", service, StringComparison.Ordinal);
        Assert.Contains("InitializeWithWindow.Initialize", service, StringComparison.Ordinal);
        Assert.Contains("TryEnterExport", service, StringComparison.Ordinal);
        Assert.Contains("if (_busy)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteSessionToolbarExposesCurrentSessionDiagnosticExport()
    {
        var xaml = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml"));
        var window = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("RemoteExportDiagnosticsButton", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnExportDiagnosticsClicked\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ExportDiagnosticsButton.IsEnabled", window, StringComparison.Ordinal);
        Assert.Contains("ExportDiagnosticsAsync", window, StringComparison.Ordinal);
        Assert.Contains(
            "handlers[ConnectionErrorActionKind.ExportDiagnostics] = RunDiagnosticExportAsync;",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "handlers[ConnectionErrorActionKind.ExportDiagnostics] = ExportDiagnosticsAsync;",
            window,
            StringComparison.Ordinal);
        Assert.Contains("await RunDiagnosticExportAsync(_lifetime.Token);", window, StringComparison.Ordinal);
        Assert.Contains("_diagnosticExportState.IsEnabled", window, StringComparison.Ordinal);
        Assert.Contains(
            "_diagnosticExportState.TryBeginExport(cancellationToken, out var exportToken)",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!_diagnosticExportState.IsClosing)\n            {\n                RefreshDiagnosticActionState();",
            window,
            StringComparison.Ordinal);
        Assert.Contains("BeginClosingDiagnostics);", window, StringComparison.Ordinal);
        Assert.Contains("_diagnosticExportState.BeginClosing()", window, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteSessionStatusExposesAccessibleConnectionQualityIndicator()
    {
        var xaml = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml"));
        var window = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

        Assert.Contains("RemoteConnectionQualityIndicator", xaml, StringComparison.Ordinal);
        Assert.Contains("<Ellipse", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QualityIndicator\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QualityText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("UpdateConnectionQualityVisual", window, StringComparison.Ordinal);
        Assert.Contains(
            "nameof(RemoteSessionViewModel.ConnectionQuality)",
            window,
            StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName", window, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteSessionToolbarSourceContractExposesRefreshPerformanceAndIndependentStatus()
    {
        // This is a source/XAML contract test. The test host does not instantiate a WinUI window.
        var xaml = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml"));
        var window = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));
        var option = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "ViewModels", "FrameRefreshOption.cs"));

        Assert.Contains("<CommandBar.Content>", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FrameRateComboBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"viewModels:FrameRefreshOption\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"RemoteFrameRateComboBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PerformanceText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FrameRateSaveStatusText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"OnFrameRateSelectionChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OnFrameRateSelectionChanged", window, StringComparison.Ordinal);
        Assert.Contains("FrameRateSaveStatusText", window, StringComparison.Ordinal);
        Assert.Contains("StatusText.SetBinding", window, StringComparison.Ordinal);
        Assert.Contains(
            "nameof(RemoteSessionViewModel.StatusMessage)",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "message => StatusText.Text = message",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StatusText.Text = \"刷新设置",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "nameof(RemoteSessionViewModel.SessionPerformance)",
            window,
            StringComparison.Ordinal);
        Assert.Contains("\"自动\"", option, StringComparison.Ordinal);
        Assert.Contains("\"120\"", option, StringComparison.Ordinal);
        Assert.Contains("\"无限\"", option, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorPresentationDoesNotReadRawExceptionMessage()
    {
        var files = new[]
        {
            RepositoryFile("src", "WinARD.Desktop", "MainWindow.xaml.cs"),
            RepositoryFile("src", "WinARD.Desktop", "Views", "ConnectionEditorDialog.xaml.cs"),
            RepositoryFile("src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"),
            RepositoryFile("src", "WinARD.Desktop", "ViewModels", "ConnectionErrorViewModel.cs"),
        };

        foreach (var file in files)
        {
            Assert.DoesNotContain("exception.Message", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ProductionErrorPathsUseNonThrowingDiagnosticWriter()
    {
        var files = new[]
        {
            RepositoryFile("src", "WinARD.Desktop", "MainWindow.xaml.cs"),
            RepositoryFile("src", "WinARD.Desktop", "Views", "ConnectionEditorDialog.xaml.cs"),
            RepositoryFile("src", "WinARD.Desktop", "ViewModels", "RemoteSessionViewModel.cs"),
            RepositoryFile("src", "WinARD.Desktop", "Services", "ConnectionAttemptWorkflow.cs"),
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain(".Write(new SafeDiagnosticEventInput", source, StringComparison.Ordinal);
            Assert.Contains(".TryWrite(new SafeDiagnosticEventInput", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MainAndEditorPresentStateBeforeBestEffortDiagnostics()
    {
        var main = File.ReadAllText(RepositoryFile("src", "WinARD.Desktop", "MainWindow.xaml.cs"));
        var editor = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "ConnectionEditorDialog.xaml.cs"));

        var connectionFailure = main.IndexOf("catch (ConnectionFailedException", StringComparison.Ordinal);
        Assert.True(connectionFailure >= 0);
        Assert.True(
            main.IndexOf("ShowConnectionError(error", connectionFailure, StringComparison.Ordinal) <
            main.IndexOf("_diagnosticSink.TryWrite", connectionFailure, StringComparison.Ordinal));
        Assert.True(
            editor.IndexOf("StatusText.Text = SafeMessage", StringComparison.Ordinal) <
            editor.IndexOf("_diagnosticSink.TryWrite", StringComparison.Ordinal));
        Assert.True(
            editor.IndexOf("ErrorCard.ViewModel = ConnectionErrorViewModel.FromError", StringComparison.Ordinal) <
            editor.IndexOf("_diagnosticSink.TryWrite", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoteWindowWiresInputFailureToSessionStopAndRetryToCancelableReconnect()
    {
        var window = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));
        var main = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "MainWindow.xaml.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("_windowLifecycle.StopSessionAsync", window, StringComparison.Ordinal);
        Assert.Contains("_windowLifecycle.DisconnectAsync", window, StringComparison.Ordinal);
        Assert.Contains("IsErrorActionEnabled", window, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ViewModel.ReportInputFailureAsync,\r\n            CloseSessionAsync",
            window,
            StringComparison.Ordinal);
        Assert.Contains("retryToken => _uiOperation.RunAsync", main, StringComparison.Ordinal);
        Assert.Contains("cancellationToken: retryToken", main, StringComparison.Ordinal);
        Assert.Contains(
            "ConnectProfileWithHandlingAsync(\n                            ownership.Profile,",
            main,
            StringComparison.Ordinal);
        Assert.Contains("profile: ownership.Profile", main, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConnectProfileWithHandlingAsync(\n                            profile,",
            main,
            StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinARD.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException("Could not locate repository root.")
            : Path.Combine([directory.FullName, .. segments]);
    }
}
