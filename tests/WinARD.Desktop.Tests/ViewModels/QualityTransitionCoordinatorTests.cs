using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Desktop.ViewModels;
using WinARD.Domain.Connections;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class QualityTransitionCoordinatorTests
{
    [Fact]
    public void Settings_take_a_defensive_encoding_snapshot()
    {
        var encodings = new List<int> { 6, 0 };
        var settings = new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, encodings, 1);

        encodings[0] = 1001;

        Assert.Equal([6, 0], settings.Encodings);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    public async Task Unsafe_boundary_never_calls_runtime(
        bool responsePresented,
        bool outstanding,
        bool activeReceive,
        bool nextRequestProduced)
    {
        var runtime = new RecordingRuntime();
        var coordinator = CreateCoordinator(runtime);

        var result = await coordinator.ApplyAtSafeBoundaryAsync(
            Decision(1, QualityLevel.Q1),
            new QualityTransitionBoundary(
                responsePresented,
                outstanding,
                activeReceive,
                nextRequestProduced),
            CancellationToken.None);

        Assert.Equal(QualityTransitionStatus.CapabilityUnavailable, result);
        Assert.Empty(runtime.Applied);
    }

    [Fact]
    public async Task Same_generation_is_applied_only_once()
    {
        var runtime = new RecordingRuntime { Result = QualityTransitionStatus.Applied };
        var coordinator = CreateCoordinator(runtime);

        var first = await coordinator.ApplyAtSafeBoundaryAsync(Decision(7, QualityLevel.Q1), SafeBoundary(), default);
        var duplicate = await coordinator.ApplyAtSafeBoundaryAsync(Decision(7, QualityLevel.Q1), SafeBoundary(), default);

        Assert.Equal(QualityTransitionStatus.Applied, first);
        Assert.Equal(QualityTransitionStatus.NoChange, duplicate);
        Assert.Single(runtime.Applied);
    }

    [Fact]
    public async Task Unobserved_rgb565_is_fail_closed_without_runtime_call()
    {
        var runtime = new RecordingRuntime();
        var capabilities = Capabilities(rgb565: CapabilitySupport.Advertised);
        var coordinator = CreateCoordinator(runtime, capabilities);

        var result = await coordinator.ApplyAtSafeBoundaryAsync(
            Decision(1, QualityLevel.Q1), SafeBoundary(), default);

        Assert.Equal(QualityTransitionStatus.CapabilityUnavailable, result);
        Assert.Empty(runtime.Applied);
    }

    [Fact]
    public async Task Unsafe_online_pixel_format_change_requires_reconnect_without_runtime_call()
    {
        var runtime = new RecordingRuntime();
        var capabilities = Capabilities(safePixel: false);
        var coordinator = CreateCoordinator(runtime, capabilities);

        var result = await coordinator.ApplyAtSafeBoundaryAsync(
            Decision(1, QualityLevel.Q1), SafeBoundary(), default);

        Assert.Equal(QualityTransitionStatus.ReconnectRequired, result);
        Assert.Empty(runtime.Applied);
    }

    [Fact]
    public async Task Scale_change_is_fail_closed_even_when_online_flag_is_true_without_resize_evidence()
    {
        var runtime = new RecordingRuntime();
        var coordinator = CreateCoordinator(runtime, Capabilities(safeScale: true));

        var result = await coordinator.ApplyAtSafeBoundaryAsync(
            Decision(1, QualityLevel.Q2), SafeBoundary(), default);

        Assert.Equal(QualityTransitionStatus.ReconnectRequired, result);
        Assert.Empty(runtime.Applied);
    }

    [Fact]
    public async Task Closed_apple_gates_never_emit_private_encodings()
    {
        var runtime = new RecordingRuntime { Result = QualityTransitionStatus.Applied };
        var capabilities = Capabilities(
            appleColor: CapabilitySupport.Observed,
            appleGray: CapabilitySupport.Observed);
        var coordinator = CreateCoordinator(runtime, capabilities, new QualityDecoderGates());

        await coordinator.ApplyAtSafeBoundaryAsync(Decision(1, QualityLevel.Q1), SafeBoundary(), default);

        Assert.DoesNotContain(1001, runtime.Applied.Single().Encodings);
        Assert.DoesNotContain(1002, runtime.Applied.Single().Encodings);
    }

    [Fact]
    public async Task Older_generation_is_stale_after_a_newer_generation_requires_reconnect()
    {
        var runtime = new RecordingRuntime { Result = QualityTransitionStatus.Applied };
        var coordinator = CreateCoordinator(runtime);

        var newer = await coordinator.ApplyAtSafeBoundaryAsync(
            Decision(10, QualityLevel.Q2), SafeBoundary(), default);
        var older = await coordinator.ApplyAtSafeBoundaryAsync(
            Decision(9, QualityLevel.Q1), SafeBoundary(), default);

        Assert.Equal(QualityTransitionStatus.ReconnectRequired, newer);
        Assert.Equal(QualityTransitionStatus.NoChange, older);
        Assert.Empty(runtime.Applied);
    }

    private static QualityTransitionCoordinator CreateCoordinator(
        RecordingRuntime runtime,
        ArdDisplayCapabilities? capabilities = null,
        QualityDecoderGates? gates = null) =>
        new(
            runtime,
            new RemoteQualitySettings(RemotePixelFormatKind.Bgra32, [6, 16, 0, 1, -239, -223], 1),
            capabilities ?? Capabilities(),
            gates ?? new QualityDecoderGates());

    private static ArdDisplayCapabilities Capabilities(
        CapabilitySupport rgb565 = CapabilitySupport.Observed,
        CapabilitySupport appleColor = CapabilitySupport.Unsupported,
        CapabilitySupport appleGray = CapabilitySupport.Unsupported,
        bool safePixel = true,
        bool safeScale = false) => new(
        CapabilitySupport.Observed,
        rgb565,
        CapabilitySupport.Observed,
        appleColor,
        appleGray,
        SafeOnlinePixelFormatSwitch: safePixel,
        SafeOnlineScaleSwitch: safeScale,
        MaximumRefreshRate: 60);

    private static QualityTransitionBoundary SafeBoundary() => new(true, false, false, false);

    private static QualityDecision Decision(long generation, QualityLevel level)
    {
        var tuple = level switch
        {
            QualityLevel.Q0 => (QualityColor.Full32, QualityScale.Native, 60),
            QualityLevel.Q1 => (QualityColor.Color16, QualityScale.Native, 60),
            QualityLevel.Q2 => (QualityColor.Color16, QualityScale.Percent75, 60),
            QualityLevel.Q3 => (QualityColor.Color16, QualityScale.Percent50, 45),
            _ => (QualityColor.Grayscale, QualityScale.Percent50, 30),
        };
        return new QualityDecision(
            generation, QualityContentState.Idle, level, tuple.Item1, tuple.Item2, tuple.Item3,
            QualityDecisionReason.Initial, true, true, false, null, null);
    }

    private sealed class RecordingRuntime : IRemoteSessionRuntime
    {
        public List<RemoteQualitySettings> Applied { get; } = [];
        public QualityTransitionStatus Result { get; set; } = QualityTransitionStatus.NoChange;
        public RemoteFramebufferSize FramebufferSize => new(100, 100);

        public ValueTask<QualityTransitionStatus> ApplyQualityTransitionAsync(
            RemoteQualitySettings settings,
            CancellationToken cancellationToken)
        {
            Applied.Add(settings);
            return ValueTask.FromResult(Result);
        }

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<RemoteServerMessage>(new RemoteBellMessage());
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }
}
