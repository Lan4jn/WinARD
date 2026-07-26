using WinARD.Application.Sessions;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Transport.Ssh;

namespace WinARD.Desktop.Services;

public sealed record ConnectionAttemptOutcome(
    ConnectionProfile Profile,
    ConnectResult Result);

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
            Exception? failure = null;
            var result = await _handler.HandleWithFailureObservationAsync(
                profile,
                stageChanged,
                exception => failure = exception,
                cancellationToken).ConfigureAwait(false);
            if (result.Error is { } error && failure is not null)
            {
                _diagnosticSink?.Write(new SafeDiagnosticEventInput(
                    error.Code,
                    error.CorrelationId,
                    "Connection attempt stage failed.",
                    [new("stage", error.Stage.ToString())],
                    failure));
            }

            if (result.Session is not null || hostKeyRetried ||
                !TryGetHostKeyFailure(failure, profile, out var verification, out var request))
            {
                return new ConnectionAttemptOutcome(profile, result);
            }

            var decision = await (hostKeyPrompt ?? _hostKeyPrompt).PromptAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var accepted = request.IsChanged
                ? decision == SshHostKeyPromptDecision.Replace
                : decision == SshHostKeyPromptDecision.Trust;
            if (!accepted)
            {
                return new ConnectionAttemptOutcome(profile, result);
            }

            var ssh = profile.SshProfile ??
                throw new InvalidOperationException("SSH 主机密钥确认缺少 SSH 配置。");
            profile = profile.WithSsh(ssh.WithHostKeyPin(verification.ToPin()));
            if (acceptedHostKey is not null)
            {
                await acceptedHostKey(profile, cancellationToken).ConfigureAwait(false);
            }

            hostKeyRetried = true;
        }
    }

    private static bool TryGetHostKeyFailure(
        Exception? exception,
        ConnectionProfile profile,
        out SshHostKeyVerification verification,
        out SshHostKeyPromptRequest request)
    {
        verification = exception switch
        {
            SshHostKeyUnknownException unknown => unknown.Verification,
            SshHostKeyChangedException { Verification: not null } changed => changed.Verification,
            _ => null!,
        };
        if (verification is null)
        {
            request = null!;
            return false;
        }

        var isChanged = verification.Status == SshHostKeyStatus.Changed;
        request = new SshHostKeyPromptRequest(
            verification.Endpoint,
            verification.Algorithm,
            verification.Fingerprint,
            isChanged ? profile.SshProfile?.HostKeyPin?.Fingerprint : null,
            isChanged);
        return true;
    }
}
