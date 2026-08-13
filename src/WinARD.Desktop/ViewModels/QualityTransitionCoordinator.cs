using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Domain.Connections;

namespace WinARD.Desktop.ViewModels;

public readonly record struct QualityTransitionBoundary(
    bool FrameResponseCompletedAndPresented,
    bool HasOutstandingFramebufferRequest,
    bool HasActiveReceive,
    bool NextFramebufferRequestProduced)
{
    public bool IsSafe =>
        FrameResponseCompletedAndPresented &&
        !HasOutstandingFramebufferRequest &&
        !HasActiveReceive &&
        !NextFramebufferRequestProduced;
}

public sealed class QualityTransitionCoordinator : IDisposable
{
    private static readonly int[] StandardEncodings = [6, 16, 0, 1, -239, -223];
    private readonly IRemoteSessionRuntime _runtime;
    private readonly ArdDisplayCapabilities _capabilities;
    private readonly QualityDecoderGates _decoderGates;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RemoteQualitySettings _currentSettings;
    private long _lastAttemptedGeneration;

    public QualityTransitionCoordinator(
        IRemoteSessionRuntime runtime,
        RemoteQualitySettings currentSettings,
        ArdDisplayCapabilities capabilities,
        QualityDecoderGates decoderGates)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _currentSettings = currentSettings ?? throw new ArgumentNullException(nameof(currentSettings));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _decoderGates = decoderGates ?? throw new ArgumentNullException(nameof(decoderGates));
    }

    public async ValueTask<QualityTransitionStatus> ApplyAtSafeBoundaryAsync(
        QualityDecision decision,
        QualityTransitionBoundary boundary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!boundary.IsSafe)
        {
            return QualityTransitionStatus.CapabilityUnavailable;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (decision.Generation <= _lastAttemptedGeneration)
            {
                return QualityTransitionStatus.NoChange;
            }

            _lastAttemptedGeneration = decision.Generation;

            var mapping = Map(decision);
            if (mapping.Status is { } rejected)
            {
                return rejected;
            }

            var settings = mapping.Settings!;
            if (SettingsEqual(_currentSettings, settings))
            {
                return QualityTransitionStatus.NoChange;
            }

            if (_currentSettings.PixelFormat != settings.PixelFormat &&
                !_capabilities.SafeOnlinePixelFormatSwitch)
            {
                return QualityTransitionStatus.ReconnectRequired;
            }

            if (_currentSettings.ScaleFactor != settings.ScaleFactor)
            {
                // No connection-specific, server-observed resize evidence is available yet.
                return QualityTransitionStatus.ReconnectRequired;
            }

            var result = await _runtime.ApplyQualityTransitionAsync(settings, cancellationToken)
                .ConfigureAwait(false);
            if (result is QualityTransitionStatus.Applied or QualityTransitionStatus.NoChange)
            {
                _currentSettings = settings;
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private (RemoteQualitySettings? Settings, QualityTransitionStatus? Status) Map(QualityDecision decision)
    {
        var pixelFormat = decision.Color switch
        {
            QualityColor.Full32 => RemotePixelFormatKind.Bgra32,
            QualityColor.Color16 => RemotePixelFormatKind.Rgb565,
            QualityColor.Grayscale => RemotePixelFormatKind.Bgra32,
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };
        if (pixelFormat == RemotePixelFormatKind.Rgb565 && !_capabilities.Rgb565.IsObserved())
        {
            return (null, QualityTransitionStatus.CapabilityUnavailable);
        }

        if (decision.Color == QualityColor.Grayscale &&
            !(_capabilities.AppleGrayscale1001.IsObserved() && _decoderGates.AppleGrayscale1001Approved))
        {
            return (null, QualityTransitionStatus.CapabilityUnavailable);
        }

        var encodings = StandardEncodings
            .Where(encoding => encoding != 6 || _capabilities.Zlib.IsObserved())
            .ToList();
        if (decision.Color == QualityColor.Grayscale)
        {
            encodings.Insert(0, 1001);
        }
        else if (_capabilities.AppleColor1002.IsObserved() && _decoderGates.AppleColor1002Approved)
        {
            encodings.Insert(0, 1002);
        }

        var scale = decision.Scale switch
        {
            QualityScale.Percent100 => 1d,
            QualityScale.Percent75 => 0.75d,
            QualityScale.Percent50 => 0.5d,
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };
        if (scale != 1 && !_capabilities.ServerScaling.IsObserved())
        {
            return (null, QualityTransitionStatus.CapabilityUnavailable);
        }

        return (new RemoteQualitySettings(pixelFormat, encodings, scale), null);
    }

    private static bool SettingsEqual(RemoteQualitySettings left, RemoteQualitySettings right) =>
        left.PixelFormat == right.PixelFormat &&
        left.ScaleFactor.Equals(right.ScaleFactor) &&
        left.Encodings.SequenceEqual(right.Encodings);

    public void Dispose() => _gate.Dispose();
}
