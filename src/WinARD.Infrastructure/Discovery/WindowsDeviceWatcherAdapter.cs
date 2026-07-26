using Windows.Devices.Enumeration;

namespace WinARD.Infrastructure.Discovery;

public enum WindowsDeviceInformationKind
{
    AssociationEndpointService,
}

public sealed record WindowsDeviceProperties(
    string Id,
    IReadOnlyDictionary<string, object?> Properties);

public interface IWindowsDeviceWatcherFactory
{
    IWindowsDeviceWatcher Create(
        string aqs,
        IReadOnlyList<string> requestedProperties,
        WindowsDeviceInformationKind kind);
}

public interface IWindowsDeviceWatcher : IDisposable
{
    event EventHandler<WindowsDeviceProperties>? Added;

    event EventHandler<WindowsDeviceProperties>? Updated;

    event EventHandler<string>? Removed;

    event EventHandler? EnumerationCompleted;

    event EventHandler? Stopped;

    void Start();

    void StopWatching();
}

public sealed class WindowsDeviceWatcherFactory : IWindowsDeviceWatcherFactory
{
    public IWindowsDeviceWatcher Create(
        string aqs,
        IReadOnlyList<string> requestedProperties,
        WindowsDeviceInformationKind kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DNS-SD discovery requires Windows.");
        }

        if (kind != WindowsDeviceInformationKind.AssociationEndpointService)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var watcher = DeviceInformation.CreateWatcher(
            aqs,
            requestedProperties,
            DeviceInformationKind.AssociationEndpointService);
        return new WindowsDeviceWatcher(watcher);
    }

    private sealed class WindowsDeviceWatcher : IWindowsDeviceWatcher
    {
        private readonly DeviceWatcher _watcher;
        private bool _disposed;

        public WindowsDeviceWatcher(DeviceWatcher watcher)
        {
            _watcher = watcher;
            _watcher.Added += OnAdded;
            _watcher.Updated += OnUpdated;
            _watcher.Removed += OnRemoved;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher.Stopped += OnStopped;
        }

        public event EventHandler<WindowsDeviceProperties>? Added;

        public event EventHandler<WindowsDeviceProperties>? Updated;

        public event EventHandler<string>? Removed;

        public event EventHandler? EnumerationCompleted;

        public event EventHandler? Stopped;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _watcher.Start();
        }

        public void StopWatching()
        {
            if (_disposed)
            {
                return;
            }

            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                _watcher.Stop();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watcher.Added -= OnAdded;
            _watcher.Updated -= OnUpdated;
            _watcher.Removed -= OnRemoved;
            _watcher.EnumerationCompleted -= OnEnumerationCompleted;
            _watcher.Stopped -= OnStopped;
            Added = null;
            Updated = null;
            Removed = null;
            EnumerationCompleted = null;
            Stopped = null;
        }

        private void OnAdded(DeviceWatcher sender, DeviceInformation information) =>
            Added?.Invoke(this, new WindowsDeviceProperties(information.Id, Copy(information.Properties)));

        private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update) =>
            Updated?.Invoke(this, new WindowsDeviceProperties(update.Id, Copy(update.Properties)));

        private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update) =>
            Removed?.Invoke(this, update.Id);

        private void OnEnumerationCompleted(DeviceWatcher sender, object args) =>
            EnumerationCompleted?.Invoke(this, EventArgs.Empty);

        private void OnStopped(DeviceWatcher sender, object args) =>
            Stopped?.Invoke(this, EventArgs.Empty);

        private static Dictionary<string, object?> Copy(IReadOnlyDictionary<string, object> properties) =>
            properties.ToDictionary(static pair => pair.Key, static pair => (object?)pair.Value, StringComparer.Ordinal);
    }
}
