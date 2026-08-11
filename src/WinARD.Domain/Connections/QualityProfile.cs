namespace WinARD.Domain.Connections;

public enum QualityPreset
{
    Automatic = 0,
    Original = 1,
    Balanced = 2,
    Smooth = 3,
    Custom = 4,
}

public enum QualityColor
{
    Automatic = 0,
    Full32 = 1,
    Color16 = 2,
    Grayscale = 3,
}

public enum QualityScale
{
    Automatic = 0,
    Native = 1,
    Percent75 = 2,
    Percent50 = 3,
}

public sealed record QualityProfile
{
    public const long MaximumTargetBytesPerSecond = 1024L * 1024 * 1024 * 1024;

    private QualityProfile(
        QualityPreset preset,
        long? targetBytesPerSecond,
        QualityColor color,
        QualityScale scale,
        FrameRefreshPolicy refresh,
        bool allowAutomaticGrayscale,
        bool bandwidthLocked,
        bool colorLocked,
        bool scaleLocked,
        bool refreshLocked)
    {
        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        if (targetBytesPerSecond is <= 0 or > MaximumTargetBytesPerSecond)
        {
            throw new ArgumentOutOfRangeException(nameof(targetBytesPerSecond));
        }

        if (!Enum.IsDefined(color))
        {
            throw new ArgumentOutOfRangeException(nameof(color));
        }

        if (!Enum.IsDefined(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        if (allowAutomaticGrayscale && colorLocked && color is QualityColor.Full32 or QualityColor.Color16)
        {
            throw new ArgumentException(
                "Automatic grayscale cannot be enabled when color is locked to a full-color format.",
                nameof(allowAutomaticGrayscale));
        }

        Preset = preset;
        TargetBytesPerSecond = targetBytesPerSecond;
        Color = color;
        Scale = scale;
        Refresh = refresh;
        AllowAutomaticGrayscale = allowAutomaticGrayscale;
        BandwidthLocked = bandwidthLocked;
        ColorLocked = colorLocked;
        ScaleLocked = scaleLocked;
        RefreshLocked = refreshLocked;
    }

    public QualityPreset Preset { get; }

    public long? TargetBytesPerSecond { get; }

    public QualityColor Color { get; }

    public QualityScale Scale { get; }

    public FrameRefreshPolicy Refresh { get; }

    public bool AllowAutomaticGrayscale { get; }

    public bool BandwidthLocked { get; }

    public bool ColorLocked { get; }

    public bool ScaleLocked { get; }

    public bool RefreshLocked { get; }

    public static QualityProfile Automatic { get; } = new(
        QualityPreset.Automatic,
        2L * 1024 * 1024,
        QualityColor.Automatic,
        QualityScale.Automatic,
        FrameRefreshPolicy.Automatic,
        true,
        false,
        false,
        false,
        false);

    public static QualityProfile Original { get; } = new(
        QualityPreset.Original,
        null,
        QualityColor.Full32,
        QualityScale.Native,
        FrameRefreshPolicy.Unlimited,
        false,
        true,
        true,
        true,
        true);

    public static QualityProfile Balanced { get; } = new(
        QualityPreset.Balanced,
        4L * 1024 * 1024,
        QualityColor.Color16,
        QualityScale.Percent75,
        FrameRefreshPolicy.Automatic,
        true,
        false,
        false,
        false,
        false);

    public static QualityProfile Smooth { get; } = new(
        QualityPreset.Smooth,
        2L * 1024 * 1024,
        QualityColor.Color16,
        QualityScale.Percent50,
        FrameRefreshPolicy.Automatic,
        true,
        false,
        false,
        false,
        false);

    public static QualityProfile CreateCustom(
        long? targetBytesPerSecond,
        QualityColor color,
        QualityScale scale,
        FrameRefreshPolicy refresh,
        bool allowAutomaticGrayscale = true,
        bool bandwidthLocked = false,
        bool colorLocked = false,
        bool scaleLocked = false,
        bool refreshLocked = false) =>
        new(
            QualityPreset.Custom,
            targetBytesPerSecond,
            color,
            scale,
            refresh,
            allowAutomaticGrayscale,
            bandwidthLocked,
            colorLocked,
            scaleLocked,
            refreshLocked);

    public QualityProfile WithRefresh(FrameRefreshPolicy refresh)
    {
        if (refresh == Refresh)
        {
            return this;
        }

        return new(
            QualityPreset.Custom,
            TargetBytesPerSecond,
            Color,
            Scale,
            refresh,
            AllowAutomaticGrayscale,
            BandwidthLocked,
            ColorLocked,
            ScaleLocked,
            RefreshLocked);
    }
}
