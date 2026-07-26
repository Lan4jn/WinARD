using WinARD.Application.Sessions;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Transport.Ssh;

namespace WinARD.Desktop.Services;

public sealed record ConnectionAttemptOutcome(
    ConnectionProfile Profile,
    ConnectResult Result,
    SshHostKeyPromptRequest? HostKeyFailure = null);

public sealed class ConnectionAttemptWorkflow(
    ConnectDeviceHandler handler,
    ISshHostKeyPrompt hostKeyPrompt,
    ISafeDiagnosticSink? diagnosticSink = null)
{
    private readonly ConnectDeviceHandler _handler = handler ??
        throw new ArgumentNullException(nameof(handler));
    private readonly ISshHostKeyPrompt _hostKeyPrompt = hostKeyPrompt ??
        throw new ArgumentNullException(nameof(hostKeyPrompt));
    private readonly ISafeDiagnosticSink? _diagnosticSink = diagnosticSink;

    public async Task<ConnectionAttemptOutcome> AttemptAsync(
        ConnectionProfile profile,
        Action<ConnectionStage>? stageChanged,
        Func<ConnectionProfile, CancellationToken, Task>? acceptedHostKey,
        CancellationToken cancellationToken) =>
        await AttemptAsync(
            profile,
            stageChanged,
            acceptedHostKey,
            hostKeyPrompt: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<ConnectionAttemptOutcome> AttemptAsync(
        ConnectionProfile profile,
        Action<ConnectionStage>? stageChanged,
        Func<ConnectionProfile, CancellationToken, Task>? acceptedHostKey,
        ISshHostKeyPrompt? hostKeyPrompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var hostKeyRetried = false;
        while (true)
        {
            HostKeyFailure? hostKeyFailure = null;
            var result = await _handler.HandleWithFailureObservationAsync(
                profile,
                stageChanged,
                observation =>
                {
                    hostKeyFailure = GetHostKeyFailure(observation.Exception, profile);
                    _diagnosticSink?.Write(new SafeDiagnosticEventInput(
                        observation.Error.Code,
                        observation.Error.CorrelationId,
                        "Connection attempt stage failed.",
                        [new("stage", observation.Error.Stage.ToString())],
                        observation.Exception));
                    return ValueTask.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);

            if (result.Session is not null || hostKeyRetried ||
                hostKeyFailure is not { } failure)
            {
                return new ConnectionAttemptOutcome(profile, result, hostKeyFailure?.Request);
            }

            var decision = await (hostKeyPrompt ?? _hostKeyPrompt)
                .PromptAsync(failure.Request, cancellationToken)
                .ConfigureAwait(false);
            var accepted = failure.Request.IsChanged
                ? decision == SshHostKeyPromptDecision.Replace
                : decision == SshHostKeyPromptDecision.Trust;
            if (!accepted)
            {
                return new ConnectionAttemptOutcome(profile, result, failure.Request);
            }

            var ssh = profile.SshProfile ??
                throw new InvalidOperationException("SSH 主机密钥确认缺少 SSH 配置。");
            profile = profile.WithSsh(ssh.WithHostKeyPin(failure.Verification.ToPin()));
            if (acceptedHostKey is not null)
            {
                await acceptedHostKey(profile, cancellationToken).ConfigureAwait(false);
            }

            hostKeyRetried = true;
        }
    }

    private static HostKeyFailure? GetHostKeyFailure(
        Exception exception,
        ConnectionProfile profile)
    {
        var verification = exception switch
        {
            SshHostKeyUnknownException unknown => unknown.Verification,
            SshHostKeyChangedException { Verification: not null } changed => changed.Verification,
            _ => null!,
        };
        if (verification is null)
        {
            return null;
        }

        var isChanged = verification.Status == SshHostKeyStatus.Changed;
        var request = new SshHostKeyPromptRequest(
            verification.Endpoint,
            verification.Algorithm,
            verification.Fingerprint,
            isChanged ? profile.SshProfile?.HostKeyPin?.Fingerprint : null,
            isChanged);
        return new HostKeyFailure(verification, request);
    }

    private sealed record HostKeyFailure(
        SshHostKeyVerification Verification,
        SshHostKeyPromptRequest Request);
}
