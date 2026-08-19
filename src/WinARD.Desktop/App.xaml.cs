using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.Services;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Desktop.Views;
using WinARD.Infrastructure.Database;
using WinARD.Infrastructure.Devices;
using WinARD.Infrastructure.Discovery;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Infrastructure.Settings;
using WinARD.Security.WindowsCredentials;
using WinARD.Security.Vault;

namespace WinARD.Desktop;

public partial class App : Microsoft.UI.Xaml.Application
{
    private ServiceProvider? _services;
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var isRemoteSessionSmoke = commandLine.Contains(
            "--remote-session-smoke",
            StringComparer.Ordinal);
        var isPresenterFailureSmoke = commandLine.Contains(
            "--remote-session-smoke-presenter-failure",
            StringComparer.Ordinal);
        var isInputFailureSmoke = commandLine.Contains(
            "--remote-session-smoke-input-failure",
            StringComparer.Ordinal);
        var isConnectionErrorSmoke = commandLine.Contains(
            "--connection-error-smoke",
            StringComparer.Ordinal);
        if (isRemoteSessionSmoke ||
            isPresenterFailureSmoke ||
            isInputFailureSmoke ||
            isConnectionErrorSmoke)
        {
            var dispatcher = new DispatcherQueueUiDispatcher(
                DispatcherQueue.GetForCurrentThread() ??
                throw new InvalidOperationException("The WinUI dispatcher is unavailable."));
            var redactor = new SecretRedactor();
            var sink = new InMemorySafeDiagnosticSink(redactor);
            var exporter = new DiagnosticExporter(sink, redactor);
            var exportService = new DiagnosticExportService(exporter);
            _window = new RemoteSessionWindow(
                new SmokeRemoteSessionRuntime(failPointerSend: isInputFailureSmoke),
                new SmokeSessionOwnership(),
                dispatcher,
                isPresenterFailureSmoke ? new SmokeFailingPresenter() : null,
                sink,
                exportService,
                isConnectionErrorSmoke
                    ? ConnectionErrorViewModel.FromError(WinARD.Domain.Errors.WinArdError.Create(
                        WinARD.Domain.Errors.ConnectionStage.Connected,
                        "REMOTE_SESSION_INTERRUPTED",
                        "Smoke-only mapped message.",
                        "smoke-correlation-123"))
                    : null,
                retryRequested: isConnectionErrorSmoke ||
                                isPresenterFailureSmoke ||
                                isInputFailureSmoke
                    ? _ =>
                    {
                        var marker = Environment.GetEnvironmentVariable(
                            "WINARD_CONNECTION_ERROR_RETRY_MARKER");
                        if (!string.IsNullOrWhiteSpace(marker))
                        {
                            File.AppendAllText(marker, "retry\n");
                        }

                        return Task.CompletedTask;
                    }
            : null);
            _window.Activate();
            return;
        }

        _services = BuildServices();
        _window = _services.GetRequiredService<MainWindow>();
        _window.Activate();
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        var dataDirectory = Environment.GetEnvironmentVariable("WINARD_DATA_DIRECTORY");
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinARD");
        }
        services.AddSingleton(new WinArdDatabase(Path.Combine(dataDirectory, "winard.db")));
        services.AddSingleton<SecretRedactor>();
        services.AddSingleton<SafeDiagnosticLevelController>();
        services.AddSingleton<ISafeDiagnosticSink>(provider => new InMemorySafeDiagnosticSink(
            provider.GetRequiredService<SecretRedactor>(),
            provider.GetRequiredService<SafeDiagnosticLevelController>()));
        services.AddSingleton<DiagnosticExporter>();
        services.AddSingleton<DiagnosticExportService>();
        services.AddSingleton<IDeviceRepository, SqliteDeviceRepository>();
        services.AddSingleton<IAppSettingsRepository, SqliteAppSettingsRepository>();
        services.AddSingleton<IBonjourServiceWatcher, DnssdServiceWatcher>();
        services.AddSingleton<IDeviceDiscovery, BonjourDeviceDiscovery>();
        var windowsStore = new WindowsCredentialStore();
        services.AddSingleton(windowsStore);
        services.AddSingleton(new VaultCredentialStoreSession(
            new FileVaultStorage(Path.Combine(dataDirectory, "credentials.vault")),
            TimeProvider.System,
            TimeSpan.FromMinutes(15)));
        services.AddSingleton<TransientCredentialStore>();
        services.AddSingleton<ITransientCredentialStore>(provider =>
            provider.GetRequiredService<TransientCredentialStore>());
        services.AddSingleton<CredentialPromptService>();
        services.AddSingleton<ICredentialStore>(provider => new RoutedCredentialStore(
            provider.GetRequiredService<WindowsCredentialStore>(),
            provider.GetRequiredService<VaultCredentialStoreSession>(),
            provider.GetRequiredService<TransientCredentialStore>(),
            provider.GetRequiredService<CredentialPromptService>(),
            provider.GetRequiredService<SecretRedactor>()));
        services.AddSingleton<IUiDispatcher>(_ => new DispatcherQueueUiDispatcher(
            DispatcherQueue.GetForCurrentThread() ??
            throw new InvalidOperationException("The WinUI dispatcher is unavailable.")));
        services.AddSingleton<IConnectionSecretProvider, CredentialStoreConnectionSecretProvider>();
        services.AddSingleton<IRemoteTransportFactory, RoutingRemoteTransport>();
        services.AddSingleton<IRfbClientFactory, RfbClientFactory>();
        services.AddSingleton<IErrorMapper, ErrorMapper>();
        services.AddSingleton<ConnectDeviceHandler>();
        services.AddSingleton<ActiveSessionCoordinator>();
        services.AddSingleton<SshHostKeyPromptService>();
        services.AddSingleton<ISshHostKeyPrompt>(provider =>
            provider.GetRequiredService<SshHostKeyPromptService>());
        services.AddSingleton<ConnectionAttemptWorkflow>();
        services.AddSingleton<ConnectionSessionController>();
        services.AddSingleton<ConnectionEditorService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class SmokeRemoteSessionRuntime(
        bool failPointerSend = false) : IRemoteSessionRuntime
    {
        private const int SmokeWidth = 3840;
        private const int SmokeHeight = 2160;
        private bool _frameSent;
        private bool _hiddenCursorSent;

        public RemoteFramebufferSize FramebufferSize => new(SmokeWidth, SmokeHeight);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (!_frameSent)
            {
                _frameSent = true;
                var pixels = new byte[SmokeWidth * SmokeHeight * 4];
                for (var y = 0; y < SmokeHeight; y++)
                {
                    for (var x = 0; x < SmokeWidth; x++)
                    {
                        var offset = ((y * SmokeWidth) + x) * 4;
                        pixels[offset] = checked((byte)(x * 255 / (SmokeWidth - 1)));
                        pixels[offset + 1] = checked((byte)(y * 255 / (SmokeHeight - 1)));
                        pixels[offset + 2] = 48;
                        pixels[offset + 3] = 255;
                    }
                }

                return new RemoteFramebufferMessage(
                    FramebufferSize,
                    pixels,
                    SmokeWidth * 4,
                    [new RemoteRectangle(0, 0, SmokeWidth, SmokeHeight)],
                    VisibleCursor());
            }

            if (!_hiddenCursorSent)
            {
                var trigger = Environment.GetEnvironmentVariable(
                    "WINARD_REMOTE_SMOKE_CURSOR_HIDE_TRIGGER");
                if (!string.IsNullOrWhiteSpace(trigger))
                {
                    while (!File.Exists(trigger))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                    }

                    _hiddenCursorSent = true;
                    return new RemoteCursorMessage(
                        new RemoteCursorUpdate(0, 0, 0, 0, []));
                }
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable smoke receive state.");
        }

        public async ValueTask SendPointerAsync(
            byte buttons,
            int x,
            int y,
            CancellationToken cancellationToken)
        {
            if (failPointerSend)
            {
                throw new IOException("sensitive smoke input failure");
            }

            var release = Environment.GetEnvironmentVariable(
                "WINARD_REMOTE_SMOKE_POINTER_SEND_RELEASE");
            if (string.IsNullOrWhiteSpace(release))
            {
                return;
            }

            while (!File.Exists(release))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }

        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;

        private static RemoteCursorUpdate VisibleCursor()
        {
            var pixels = new byte[3 * 3 * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = 32;
                pixels[index + 1] = 220;
                pixels[index + 2] = 255;
                pixels[index + 3] = 255;
            }

            return new RemoteCursorUpdate(1, 1, 3, 3, pixels);
        }
    }

    private sealed class SmokeFailingPresenter : Rendering.IFramePresenter
    {
        public void Resize(int width, int height) { }

        public void Present(
            ReadOnlySpan<byte> bgra32,
            int stride,
            IReadOnlyList<RemoteRectangle> dirtyRectangles) =>
            throw new InvalidOperationException("sensitive smoke presenter failure");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SmokeSessionOwnership : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                var marker = Environment.GetEnvironmentVariable("WINARD_REMOTE_SMOKE_MARKER");
                if (!string.IsNullOrWhiteSpace(marker))
                {
                    File.WriteAllText(marker, "released");
                }
            }

            return ValueTask.CompletedTask;
        }
    }

}
