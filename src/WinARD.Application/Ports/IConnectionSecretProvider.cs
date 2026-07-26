using WinARD.Domain.Connections;

namespace WinARD.Application.Ports;

public interface IConnectionSecretProvider
{
    ValueTask<ISecret> GetSecretAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);
}
