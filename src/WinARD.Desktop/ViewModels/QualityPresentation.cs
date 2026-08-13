using System.Collections.ObjectModel;
using System.Globalization;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Domain.Connections;

namespace WinARD.Desktop.ViewModels;

public enum QualityPresentationStatus
{
    Unknown,
    TargetReached,
    ReducingForMotion,
    RecoveringClarity,
    TargetUnsatisfied,
    CapabilityLimited,
    ReconnectRequired,
    SafeFallback,
    Unrestricted,
}

public sealed record QualityChoice<T>(
    T Value,
    string DisplayName,
    bool IsEnabled = true,
    string? ConstraintText = null);

public enum QualityBandwidthOptionKind
{
    Preset,
    Unlimited,
    Custom,
}

public sealed record QualityBandwidthOption(
    string Id,
    QualityBandwidthOptionKind Kind,
    long? Value,
    string DisplayName);

public enum QualityActualEncoding
{
    Unknown,
    Raw,
    Zlib,
    Zrle,
    AppleGrayscale,
    AppleColor,
}

public sealed record QualityActualState(
    RemotePixelFormatKind PixelFormat,
    QualityActualEncoding Encoding,
    bool FallbackUsed)
{
    public QualityScale Scale { get; init; } = QualityScale.Percent100;
}

public sealed record QualityPresentationSnapshot(
    QualityProfile Profile,
    QualityDecision? Decision,
    QualityTransitionStatus TransitionStatus,
    SessionPerformanceSnapshot Performance,
    long Epoch,
    long Version)
{
    public QualityPresentationSnapshot(
        QualityProfile profile,
        QualityDecision? decision,
        QualityTransitionStatus transitionStatus,
        SessionPerformanceSnapshot performance,
        long epoch,
        long version,
        QualityActualState? actual)
        : this(profile, decision, transitionStatus, performance, epoch, version)
    {
        Actual = actual;
    }

    public QualityPresentationSnapshot(
        QualityProfile profile,
        QualityDecision? decision,
        QualityTransitionStatus transitionStatus,
        SessionPerformanceSnapshot performance,
        long epoch,
        long version,
        QualityActualState? actual,
        bool pendingReconnect)
        : this(profile, decision, transitionStatus, performance, epoch, version, actual)
    {
        PendingReconnect = pendingReconnect;
    }

    public QualityActualState? Actual { get; init; }

    public bool PendingReconnect { get; }
}

public static class QualityPresentation
{
    private const long MiB = 1024L * 1024;

    public static IReadOnlyList<QualityChoice<QualityPreset>> PresetOptions { get; } =
        ReadOnly<QualityPreset>([
            new(QualityPreset.Automatic, "自动"),
            new(QualityPreset.Original, "原画"),
            new(QualityPreset.Balanced, "平衡"),
            new(QualityPreset.Smooth, "流畅"),
            new(QualityPreset.Custom, "自定义"),
        ]);

    public static IReadOnlyList<QualityBandwidthOption> BandwidthOptions { get; } =
        new ReadOnlyCollection<QualityBandwidthOption>([
            new("preset-1", QualityBandwidthOptionKind.Preset, 1 * MiB, "1 MiB/s"),
            new("preset-2", QualityBandwidthOptionKind.Preset, 2 * MiB, "2 MiB/s"),
            new("preset-4", QualityBandwidthOptionKind.Preset, 4 * MiB, "4 MiB/s"),
            new("preset-8", QualityBandwidthOptionKind.Preset, 8 * MiB, "8 MiB/s"),
            new("preset-16", QualityBandwidthOptionKind.Preset, 16 * MiB, "16 MiB/s"),
            new("unlimited", QualityBandwidthOptionKind.Unlimited, null, "不限制"),
            new("custom", QualityBandwidthOptionKind.Custom, null, "自定义…"),
        ]);

    public static IReadOnlyList<QualityChoice<QualityColor>> ColorOptions { get; } =
        ReadOnly<QualityColor>([
            new(QualityColor.Automatic, "自动"),
            new(QualityColor.Full32, "32 位"),
            new(QualityColor.Color16, "16 位"),
            new(QualityColor.Grayscale, "灰度"),
        ]);

    public static IReadOnlyList<QualityChoice<QualityScale>> ScaleOptions { get; } =
        ReadOnly<QualityScale>([
            new(QualityScale.Automatic, "自动"),
            new(QualityScale.Percent100, "100%"),
            new(QualityScale.Percent75, "75%"),
            new(QualityScale.Percent50, "50%"),
            new(QualityScale.Percent25, "25%"),
        ]);

    public static IReadOnlyList<QualityChoice<QualityColor>> ColorOptionsFor(
        QualityColor selected,
        ArdDisplayCapabilities capabilities,
        QualityDecoderGates decoderGates)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(decoderGates);
        var grayscaleAvailable = capabilities.AppleGrayscale1001.IsObserved() &&
            decoderGates.AppleGrayscale1001Approved;
        return ColorOptions
            .Select(option => option.Value == QualityColor.Grayscale && !grayscaleAvailable
                ? Unavailable(option, option.Value == selected)
                : option)
            .ToArray();
    }

    public static IReadOnlyList<QualityChoice<QualityScale>> ScaleOptionsFor(
        QualityScale selected,
        ArdDisplayCapabilities capabilities,
        bool scaleSwitchApproved = false)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var scalingAvailable = capabilities.ServerScaling.IsObserved() && scaleSwitchApproved;
        return ScaleOptions
            .Select(option => option.Value is QualityScale.Percent75 or QualityScale.Percent50 or QualityScale.Percent25 && !scalingAvailable
                ? Unavailable(option, option.Value == selected)
                : option)
            .ToArray();
    }

    public static string FormatSummary(
        QualityPreset preset,
        QualityColor color,
        QualityScale scale,
        int? targetFps,
        double? bytesPerSecond) =>
        $"{PresetName(preset)} · {ColorName(color)} · {ScaleName(scale)} · " +
        $"{(targetFps is > 0 ? $"{targetFps} FPS" : "—")} · " +
        (bytesPerSecond is >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / MiB:0.0} MiB/s")
            : "—");

    public static string FormatPendingSummary(
        QualityProfile profile,
        int? targetFps,
        double? bytesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return $"设置：{FormatSummary(profile.Preset, profile.Color, profile.Scale, targetFps, bytesPerSecond)}" +
            "（待能力确认）";
    }

    public static string DesiredText(QualityProfile profile, QualityDecision? decision)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var color = decision?.Color ?? profile.Color;
        var scale = decision?.Scale ?? profile.Scale;
        var targetFps = decision?.TargetFramesPerSecond ?? profile.Refresh.FixedFramesPerSecond;
        var bandwidth = profile.TargetBytesPerSecond is { } target
            ? string.Create(CultureInfo.InvariantCulture, $"{target / (double)MiB:0.#} MiB/s")
            : "不限制";
        return $"期望：{ColorName(color)} · {ScaleName(scale)} · " +
            $"{(targetFps is > 0 ? $"{targetFps} FPS" : "—")} · {bandwidth}";
    }

    public static string ScaleStateText(
        QualityScale saved,
        QualityScale resolved,
        QualityScale actual,
        bool pendingReconnect,
        bool safeFallback)
    {
        var fallback = safeFallback ? "（已安全回退）" : string.Empty;
        var pending = pendingReconnect ? " · 需要重新连接后生效" : string.Empty;
        return $"设置：{ScaleName(saved)} · 本连接解析：{ScaleName(resolved)} · " +
            $"本连接实际：{ScaleName(actual)}{fallback}{pending}";
    }

    public static string AppliedText(
        QualityActualState? actual,
        SessionPerformanceSnapshot performance)
    {
        ArgumentNullException.ThrowIfNull(performance);
        var color = actual?.PixelFormat switch
        {
            RemotePixelFormatKind.Bgra32 => "32 位",
            RemotePixelFormatKind.Rgb565 => "16 位",
            _ => "Unknown",
        };
        var fallback = actual?.FallbackUsed == true ? "（已安全回退）" : string.Empty;
        var encoding = ActualEncodingName(actual?.Encoding ?? QualityActualEncoding.Unknown);
        var hasSample = performance.SampleSequence > 0;
        var actualFps = hasSample && performance.ActualFramesPerSecond > 0
            ? $"{performance.ActualFramesPerSecond} FPS"
            : "—";
        var bandwidth = hasSample && performance.ReceiveBytesPerSecond > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{performance.ReceiveBytesPerSecond / (double)MiB:0.0} MiB/s")
            : "—";
        return $"实际：{color}{fallback} · {encoding} · {actualFps} · {bandwidth}";
    }

    public static string StatusText(QualityPresentationStatus status) => status switch
    {
        QualityPresentationStatus.TargetReached => "已达到目标",
        QualityPresentationStatus.ReducingForMotion => "正在为运动画面降载",
        QualityPresentationStatus.RecoveringClarity => "正在恢复清晰度",
        QualityPresentationStatus.TargetUnsatisfied => "当前链路无法满足目标",
        QualityPresentationStatus.CapabilityLimited => "服务器能力不足，自动范围已限制",
        QualityPresentationStatus.ReconnectRequired => "下次连接生效",
        QualityPresentationStatus.SafeFallback => "本连接已安全回退",
        QualityPresentationStatus.Unrestricted => "带宽不限制",
        _ => "状态未知",
    };

    public static QualityPresentationStatus StatusFor(
        QualityDecisionReason reason,
        bool targetSatisfied,
        QualityTransitionStatus transitionStatus) =>
        StatusFor(reason, targetSatisfied, transitionStatus, fallbackUsed: false);

    public static QualityPresentationStatus StatusFor(
        QualityDecisionReason reason,
        bool targetSatisfied,
        QualityTransitionStatus transitionStatus,
        bool fallbackUsed) =>
        StatusFor(
            reason,
            targetSatisfied,
            transitionStatus,
            fallbackUsed,
            hasNumericBandwidthTarget: true);

    public static QualityPresentationStatus StatusFor(
        QualityDecisionReason reason,
        bool targetSatisfied,
        QualityTransitionStatus transitionStatus,
        bool fallbackUsed,
        bool hasNumericBandwidthTarget)
    {
        if (fallbackUsed)
        {
            return QualityPresentationStatus.SafeFallback;
        }
        if (transitionStatus == QualityTransitionStatus.ReconnectRequired)
        {
            return QualityPresentationStatus.ReconnectRequired;
        }
        if (transitionStatus == QualityTransitionStatus.CapabilityUnavailable)
        {
            return QualityPresentationStatus.CapabilityLimited;
        }
        if (!hasNumericBandwidthTarget && targetSatisfied)
        {
            return QualityPresentationStatus.Unrestricted;
        }

        return reason switch
        {
            QualityDecisionReason.StableRecovery => QualityPresentationStatus.RecoveringClarity,
            QualityDecisionReason.MotionDetected or
                QualityDecisionReason.SustainedOverTarget or
                QualityDecisionReason.SevereOverTarget => QualityPresentationStatus.ReducingForMotion,
            QualityDecisionReason.CapabilityLimited => QualityPresentationStatus.CapabilityLimited,
            QualityDecisionReason.TargetUnsatisfied => QualityPresentationStatus.TargetUnsatisfied,
            _ when targetSatisfied => QualityPresentationStatus.TargetReached,
            _ => QualityPresentationStatus.TargetUnsatisfied,
        };
    }

    public static QualityPresentationStatus StatusFor(QualityPresentationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.PendingReconnect)
        {
            return QualityPresentationStatus.ReconnectRequired;
        }
        if (snapshot.Actual?.FallbackUsed == true)
        {
            return QualityPresentationStatus.SafeFallback;
        }
        if (snapshot.Decision is null)
        {
            return QualityPresentationStatus.Unknown;
        }

        return StatusFor(
            snapshot.Decision.Reason,
            snapshot.Decision.TargetSatisfied,
            snapshot.TransitionStatus,
            fallbackUsed: false,
            snapshot.Profile.TargetBytesPerSecond is not null);
    }

    public static string AutomationName(string summary, QualityPresentationStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        return $"画质：{summary}；状态：{StatusText(status)}";
    }

    public static string AutomationName(
        string desired,
        string applied,
        QualityPresentationStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desired);
        ArgumentException.ThrowIfNullOrWhiteSpace(applied);
        return $"画质：{desired}；{applied}；状态：{StatusText(status)}";
    }

    public static string EncodingName(int? encoding) => encoding switch
    {
        null => "Unknown",
        0 => "Raw",
        6 => "Zlib",
        16 => "ZRLE",
        1001 => "Apple 灰度",
        1002 => "Apple 彩色",
        _ => "Unknown",
    };

    public static string ActualEncodingName(QualityActualEncoding encoding) => encoding switch
    {
        QualityActualEncoding.Raw => "Raw",
        QualityActualEncoding.Zlib => "Zlib",
        QualityActualEncoding.Zrle => "ZRLE",
        QualityActualEncoding.AppleGrayscale => "Apple 灰度",
        QualityActualEncoding.AppleColor => "Apple 彩色",
        _ => "Unknown",
    };

    public static QualityProfile ApplyPreset(QualityPreset preset) => preset switch
    {
        QualityPreset.Automatic => QualityProfile.Automatic,
        QualityPreset.Original => QualityProfile.Original,
        QualityPreset.Balanced => QualityProfile.Balanced,
        QualityPreset.Smooth => QualityProfile.Smooth,
        _ => throw new ArgumentException("自定义预设需要具体的详细设置。", nameof(preset)),
    };

    public static QualityProfile AsCustom(QualityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return QualityProfile.CreateCustom(
            profile.TargetBytesPerSecond,
            profile.Color,
            profile.Scale,
            profile.Refresh,
            profile.AllowAutomaticGrayscale,
            profile.BandwidthLocked,
            profile.ColorLocked,
            profile.ScaleLocked,
            profile.RefreshLocked);
    }

    public static string SanitizePerformanceText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        const string marker = "编码 ";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        var valueStart = start + marker.Length;
        var end = text.IndexOf(" ·", valueStart, StringComparison.Ordinal);
        if (end < 0 || !int.TryParse(
            text.AsSpan(valueStart, end - valueStart),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out _))
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, start), "Unknown", text.AsSpan(end));
    }

    public static string ConnectionQualityAutomationName(string displayText, string performanceText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayText);
        ArgumentNullException.ThrowIfNull(performanceText);
        return $"连接质量：{displayText}；会话性能：{SanitizePerformanceText(performanceText)}";
    }

    public static QualityProfile WithBandwidth(QualityProfile profile, long? value) =>
        QualityProfile.CreateCustom(
            value,
            profile.Color,
            profile.Scale,
            profile.Refresh,
            profile.AllowAutomaticGrayscale,
            profile.BandwidthLocked,
            profile.ColorLocked,
            profile.ScaleLocked,
            profile.RefreshLocked);

    public static QualityProfile WithColor(QualityProfile profile, QualityColor value) =>
        Custom(profile, color: value);

    public static QualityProfile WithScale(QualityProfile profile, QualityScale value) =>
        Custom(profile, scale: value);

    public static QualityProfile WithRefresh(QualityProfile profile, FrameRefreshPolicy value) =>
        Custom(profile, refresh: value);

    public static QualityProfile WithAutomaticGrayscale(QualityProfile profile, bool value) =>
        Custom(profile, allowAutomaticGrayscale: value);

    public static QualityProfile WithLocks(
        QualityProfile profile,
        bool bandwidthLocked,
        bool colorLocked,
        bool scaleLocked,
        bool refreshLocked) => QualityProfile.CreateCustom(
            profile.TargetBytesPerSecond,
            profile.Color,
            profile.Scale,
            profile.Refresh,
            profile.AllowAutomaticGrayscale && !(colorLocked && profile.Color is QualityColor.Full32 or QualityColor.Color16),
            bandwidthLocked,
            colorLocked,
            scaleLocked,
            refreshLocked);

    public static IReadOnlyList<QualityChoice<FrameRefreshPolicy>> RefreshOptions(
        FrameRefreshPolicy selected,
        int? remoteMaximum) => FrameRefreshOptions.Create(selected, remoteMaximum)
            .Select(option => new QualityChoice<FrameRefreshPolicy>(
                option.Policy,
                option.DisplayName,
                option.IsEnabled,
                option.ConstraintText))
            .ToArray();

    private static QualityProfile Custom(
        QualityProfile profile,
        long? targetBytesPerSecond = null,
        QualityColor? color = null,
        QualityScale? scale = null,
        FrameRefreshPolicy? refresh = null,
        bool? allowAutomaticGrayscale = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var nextColor = color ?? profile.Color;
        var nextAllowGrayscale = allowAutomaticGrayscale ?? profile.AllowAutomaticGrayscale;
        if (profile.ColorLocked && nextColor is QualityColor.Full32 or QualityColor.Color16)
        {
            nextAllowGrayscale = false;
        }

        return QualityProfile.CreateCustom(
            targetBytesPerSecond ?? profile.TargetBytesPerSecond,
            nextColor,
            scale ?? profile.Scale,
            refresh ?? profile.Refresh,
            nextAllowGrayscale,
            profile.BandwidthLocked,
            profile.ColorLocked,
            profile.ScaleLocked,
            profile.RefreshLocked);
    }

    private static string PresetName(QualityPreset value) =>
        PresetOptions.FirstOrDefault(option => option.Value == value)?.DisplayName ?? "Unknown";

    private static string ColorName(QualityColor value) =>
        ColorOptions.FirstOrDefault(option => option.Value == value)?.DisplayName ?? "Unknown";

    private static string ScaleName(QualityScale value) =>
        ScaleOptions.FirstOrDefault(option => option.Value == value)?.DisplayName ?? "Unknown";

    private static ReadOnlyCollection<QualityChoice<T>> ReadOnly<T>(QualityChoice<T>[] values) =>
        new ReadOnlyCollection<QualityChoice<T>>(values);

    private static QualityChoice<T> Unavailable<T>(QualityChoice<T> option, bool saved) =>
        option with
        {
            IsEnabled = false,
            ConstraintText = saved ? "已保存，当前连接不可用" : "当前连接不可用",
        };
}
