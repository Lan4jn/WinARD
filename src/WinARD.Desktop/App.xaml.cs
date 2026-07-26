using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinARD.Application.Errors;
using WinARD.Application.Ports;
using WinARD.Application.Sessions;
using WinARD.Desktop.Services;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Infrastructure.Database;
using WinARD.Infrastructure.Devices;
using WinARD.Infrastructure.Discovery;
using WinARD.Security.WindowsCredentials;
using WinARD.Transport.Tcp;

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
        _services = BuildServices();
        _window = _services.GetRequiredService<MainWindow>();
        _window.Activate();
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinARD");
        services.AddSingleton(new WinArdDatabase(Path.Combine(dataDirectory, "winard.db")));
        services.AddSingleton<IDeviceRepository, SqliteDeviceRepository>();
        services.AddSingleton<IBonjourServiceWatcher, DnssdServiceWatcher>();
        services.AddSingleton<IDeviceDiscovery, BonjourDeviceDiscovery>();
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<IUiDispatcher>(_ => new DispatcherQueueUiDispatcher(
            DispatcherQueue.GetForCurrentThread() ??
            throw new InvalidOperationException("The WinUI dispatcher is unavailable.")));
        services.AddSingleton<IConnectionSecretProvider, CredentialStoreConnectionSecretProvider>();
        services.AddSingleton<IRemoteTransportFactory, TcpRemoteTransport>();
        services.AddSingleton<IRfbClientFactory, UnavailableRfbClientFactory>();
        services.AddSingleton<IErrorMapper, ErrorMapper>();
        services.AddSingleton<ConnectDeviceHandler>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(validateScopes: true);
    }
}
