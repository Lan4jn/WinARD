using WinARD.Application.Sessions;
using WinARD.Remote.Protocol.Errors;

namespace WinARD.Desktop.Services;

public static class RuntimeReconnectFailureClassifier
{
    public static bool IsTransient(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return ReconnectFailureClassifier.IsTransient(failure) ||
            failure is RfbProtocolException
            {
                Failure.Kind: RfbProtocolFailureKind.RemoteSessionClosed
            } ||
            failure is RfbProtocolException
            {
                Failure.Kind: RfbProtocolFailureKind.TruncatedRead,
                InnerException: IOException
            };
    }
}
