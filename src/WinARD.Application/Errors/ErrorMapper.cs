using System.Net.Sockets;
using WinARD.Domain.Errors;

namespace WinARD.Application.Errors;

public interface IErrorMapper
{
    WinArdError Map(Exception exception, ConnectionStage stage);
}

public sealed class ErrorMapper : IErrorMapper
{
    private readonly Func<string> _correlationIdFactory;

    public ErrorMapper()
        : this(() => Guid.NewGuid().ToString("N"))
    {
    }

    public ErrorMapper(Func<string> correlationIdFactory)
    {
        _correlationIdFactory = correlationIdFactory
            ?? throw new ArgumentNullException(nameof(correlationIdFactory));
    }

    public WinArdError Map(Exception exception, ConnectionStage stage)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var (code, message) = Classify(exception);
        return WinArdError.Create(stage, code, message, _correlationIdFactory());
    }

    private static (string Code, string Message) Classify(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return ("CONNECTION_INTERRUPTED", "The connection ended before it completed.");
        }

        if (exception is SocketException socketException &&
            socketException.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or
                SocketError.NoData or SocketError.NoRecovery)
        {
            return ("DNS_RESOLUTION_FAILED", "The remote host could not be resolved.");
        }

        if (exception is SocketException)
        {
            return ("TCP_CONNECTION_FAILED", "A TCP connection to the remote host could not be established.");
        }

        var exceptionType = exception.GetType().FullName;
        if (exceptionType == "WinARD.Transport.Ssh.OpenSshAuthenticationUnsupportedException")
        {
            return ("SSH_AUTH_UNSUPPORTED", "The configured SSH authentication method is not supported.");
        }

        if (exceptionType?.StartsWith("WinARD.Transport.Ssh.OpenSsh", StringComparison.Ordinal) == true)
        {
            return ("SSH_CONNECTION_FAILED", "The SSH tunnel could not be established.");
        }

        return exceptionType switch
        {
            "WinARD.Transport.TransportTimeoutException" =>
                ("TRANSPORT_TIMEOUT", "The remote transport connection timed out."),
            "WinARD.Transport.Ssh.SshHostKeyUnknownException" =>
                ("SSH_HOST_KEY_UNKNOWN", "The SSH host key has not been trusted yet."),
            "WinARD.Transport.Ssh.SshHostKeyChangedException" =>
                ("SSH_HOST_KEY_CHANGED", "The SSH host key no longer matches the trusted key."),
            "WinARD.Remote.Protocol.Errors.UnsupportedRfbVersionException" =>
                ("RFB_VERSION_UNSUPPORTED", "The server uses an unsupported RFB version."),
            "WinARD.Remote.Protocol.Errors.UnsupportedSecurityTypeException" =>
                ("RFB_SECURITY_UNSUPPORTED", "The server does not support Apple Remote Desktop authentication."),
            "WinARD.Remote.Protocol.Errors.RfbConnectionRejectedException" =>
                ("RFB_CONNECTION_REJECTED", "The server rejected the RFB connection."),
            "WinARD.Remote.Protocol.Errors.ArdAuthenticationRejectedException" =>
                ("ARD_AUTH_REJECTED", "The Mac rejected the supplied credentials."),
            "WinARD.Remote.Protocol.Errors.ArdExtendedInitializationRequiredException" =>
                ("ARD_EXTENDED_INIT_REQUIRED", "The Mac requires Apple Remote Desktop extended initialization before control can begin."),
            "WinARD.Remote.Protocol.Errors.ArdControlNotAllowedException" =>
                ("ARD_CONTROL_NOT_ALLOWED", "The Mac did not allow control for this session."),
            "WinARD.Remote.Protocol.Errors.ArdSessionCommandUnavailableException" =>
                ("ARD_SESSION_COMMAND_UNAVAILABLE", "The Mac does not support selecting a console session."),
            "WinARD.Remote.Protocol.Errors.ArdSessionDeniedException" =>
                ("ARD_SESSION_DENIED", "The Mac denied access to the console session."),
            "WinARD.Remote.Protocol.Errors.ArdSessionMalformedException" =>
                ("ARD_SESSION_MALFORMED", "The Mac returned an invalid console session response."),
            "WinARD.Remote.Protocol.Errors.RfbProtocolException" =>
                ("RFB_PROTOCOL_ERROR", "The server sent an invalid RFB protocol message."),
            _ => ("UNEXPECTED_CONNECTION_ERROR", "The connection failed unexpectedly."),
        };
    }
}
