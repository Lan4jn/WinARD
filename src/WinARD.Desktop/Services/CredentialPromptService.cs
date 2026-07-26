using WinARD.Application.Ports;
using WinARD.Domain.Connections;

namespace WinARD.Desktop.Services;

public sealed class CredentialPromptService
{
    private readonly object _gate = new();
    private Func<ConnectionProfile, CancellationToken, ValueTask<ISecret>>? _handler;

    public void SetHandler(Func<ConnectionProfile, CancellationToken, ValueTask<ISecret>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handler = handler;
        }
    }

    public void ClearHandler()
    {
        lock (_gate)
        {
            _handler = null;
        }
    }

    public ValueTask<ISecret> PromptAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        Func<ConnectionProfile, CancellationToken, ValueTask<ISecret>>? handler;
        lock (_gate)
        {
            handler = _handler;
        }

        return handler is null
            ? ValueTask.FromException<ISecret>(new InvalidOperationException("凭据提示窗口当前不可用。"))
            : handler(profile, cancellationToken);
    }
}
