using System.Globalization;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Application.Sessions;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Remote.Protocol.Errors;
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
        var bootstrapAttempt = QualityBootstrapAttempt.Preferred;
        QualityBootstrapFailureReason? preferredFailureReason = null;
        while (true)
        {
            HostKeyFailure? hostKeyFailure = null;
            QualityBootstrapCompatibilityException? compatibilityFailure = null;
            var result = await _handler.HandleWithFailureObservationAsync(
                profile,
                bootstrapAttempt,
                preferredFailureReason,
                stageChanged,
                observation =>
                {
                    hostKeyFailure = GetHostKeyFailure(observation.Exception, profile);
                    compatibilityFailure = observation.Exception as QualityBootstrapCompatibilityException;
                    if (bootstrapAttempt != QualityBootstrapAttempt.Fallback &&
                        compatibilityFailure is null)
                    {
                        _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
                            observation.Error.Code,
                            observation.Error.CorrelationId,
                            "Connection attempt stage failed.",
                            CreateFailureFields(
                                observation.Error.Stage,
                                observation.Exception,
                                bootstrapAttempt,
                                preferredFailureReason),
                            observation.Exception));
                    }

                    return ValueTask.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);

            if (result.Session is not null)
            {
                return new ConnectionAttemptOutcome(profile, result);
            }

            if (bootstrapAttempt == QualityBootstrapAttempt.Preferred &&
                compatibilityFailure is { } preferredFailure)
            {
                preferredFailureReason = preferredFailure.Reason;
                _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
                    "QUALITY_BOOTSTRAP_FALLBACK",
                    result.Error?.CorrelationId ?? string.Empty,
                    "Preferred bootstrap quality was incompatible; retrying with safe quality.",
                    [
                        new DiagnosticField("BootstrapAttempt", QualityBootstrapAttempt.Preferred.ToString()),
                        new DiagnosticField("BootstrapFallbackReason", preferredFailure.Reason.ToString()),
                    ]));
                bootstrapAttempt = QualityBootstrapAttempt.Fallback;
                continue;
            }

            if (hostKeyRetried || hostKeyFailure is not { } failure)
            {
                WriteTerminalFallbackFailure(result, bootstrapAttempt, preferredFailureReason, compatibilityFailure);
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
                WriteTerminalFallbackFailure(result, bootstrapAttempt, preferredFailureReason, compatibilityFailure);
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

    private static List<DiagnosticField> CreateFailureFields(
        ConnectionStage stage,
        Exception exception,
        QualityBootstrapAttempt bootstrapAttempt,
        QualityBootstrapFailureReason? preferredFailureReason)
    {
        var fields = new List<DiagnosticField>(7)
        {
            new("stage", stage.ToString()),
        };
        if (bootstrapAttempt == QualityBootstrapAttempt.Fallback)
        {
            fields.Add(new DiagnosticField("BootstrapAttempt", bootstrapAttempt.ToString()));
            fields.Add(new DiagnosticField("FallbackFailed", bool.TrueString));
            if (preferredFailureReason is { } reason)
            {
                fields.Add(new DiagnosticField("PreferredFailureReason", reason.ToString()));
            }
        }

        if (exception is not RfbProtocolException { Failure: { } failure })
        {
            return fields;
        }

        fields.Add(new DiagnosticField("ProtocolFailureKind", failure.Kind.ToString()));
        if (failure.HandshakeStage is { } handshakeStage)
        {
            fields.Add(new DiagnosticField("RfbHandshakeStage", handshakeStage.ToString()));
        }

        if (failure.ExpectedByteCount is { } expectedByteCount)
        {
            fields.Add(new DiagnosticField(
                "ExpectedByteCount",
                expectedByteCount.ToString(CultureInfo.InvariantCulture)));
        }

        if (failure.ActualByteCount is { } actualByteCount)
        {
            fields.Add(new DiagnosticField(
                "ActualByteCount",
                actualByteCount.ToString(CultureInfo.InvariantCulture)));
        }

        return fields;
    }

    private static List<DiagnosticField> CreateFallbackFailureFields(
        QualityBootstrapFailureReason preferredFailureReason,
        QualityBootstrapFailureReason? fallbackFailureReason)
    {
        var fields = new List<DiagnosticField>(4)
        {
            new("BootstrapAttempt", QualityBootstrapAttempt.Fallback.ToString()),
            new("FallbackFailed", bool.TrueString),
            new("PreferredFailureReason", preferredFailureReason.ToString()),
        };
        if (fallbackFailureReason is { } reason)
        {
            fields.Add(new DiagnosticField("BootstrapFallbackReason", reason.ToString()));
        }

        return fields;
    }

    private void WriteTerminalFallbackFailure(
        ConnectResult result,
        QualityBootstrapAttempt bootstrapAttempt,
        QualityBootstrapFailureReason? preferredFailureReason,
        QualityBootstrapCompatibilityException? compatibilityFailure)
    {
        if (bootstrapAttempt != QualityBootstrapAttempt.Fallback ||
            preferredFailureReason is not { } preferredReason)
        {
            return;
        }

        _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
            "QUALITY_BOOTSTRAP_FALLBACK",
            result.Error?.CorrelationId ?? string.Empty,
            "Safe bootstrap fallback failed.",
            CreateFallbackFailureFields(preferredReason, compatibilityFailure?.Reason)));
    }

    private sealed record HostKeyFailure(
        SshHostKeyVerification Verification,
        SshHostKeyPromptRequest Request);
}
