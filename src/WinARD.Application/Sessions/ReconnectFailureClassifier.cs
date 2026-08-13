using WinARD.Domain.Errors;

namespace WinARD.Application.Sessions;

public sealed class ReconnectFailureException(WinArdError error) : Exception(error.UserMessage)
{
    public WinArdError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
}

public static class ReconnectFailureClassifier
{
    private static readonly HashSet<string> TransientCodes = new(StringComparer.Ordinal)
    {
        "REMOTE_SESSION_INTERRUPTED",
        "TCP_CONNECTION_FAILED",
        "TRANSPORT_TIMEOUT",
        "DNS_RESOLUTION_FAILED",
        "SSH_CONNECTION_FAILED",
        "UNEXPECTED_CONNECTION_ERROR",
    };

    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is IOException ||
            exception is System.Net.Sockets.SocketException ||
            exception is ReconnectFailureException failure &&
            TransientCodes.Contains(failure.Error.Code);
    }
}
