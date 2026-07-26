using WinARD.Desktop.ViewModels;

namespace WinARD.Desktop.Services;

public sealed class ConnectionErrorActionHandler
{
    private readonly Dictionary<ConnectionErrorActionKind, Func<CancellationToken, Task>> _handlers;

    public ConnectionErrorActionHandler(
        IEnumerable<KeyValuePair<ConnectionErrorActionKind, Func<CancellationToken, Task>>> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    public bool CanHandle(ConnectionErrorActionKind action) => _handlers.ContainsKey(action);

    public Task HandleAsync(ConnectionErrorActionKind action, CancellationToken cancellationToken) =>
        _handlers.TryGetValue(action, out var handler)
            ? handler(cancellationToken)
            : Task.FromException(new InvalidOperationException($"No handler is registered for {action}."));
}
