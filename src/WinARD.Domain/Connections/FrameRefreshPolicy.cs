namespace WinARD.Domain.Connections;

public enum FrameRefreshMode
{
    Automatic = 0,
    Fixed = 1,
    Unlimited = 2,
}

public readonly record struct FrameRefreshPolicy
{
    private static readonly int[] SupportedFixedValues = [30, 45, 60, 75, 90, 105, 120];
    private static readonly IReadOnlyList<int> SupportedFixedValuesView = Array.AsReadOnly(SupportedFixedValues);

    private FrameRefreshPolicy(FrameRefreshMode mode, int? fixedFramesPerSecond)
    {
        Mode = mode;
        FixedFramesPerSecond = fixedFramesPerSecond;
    }

    public FrameRefreshMode Mode { get; }

    public int? FixedFramesPerSecond { get; }

    public static FrameRefreshPolicy Automatic { get; } = new(FrameRefreshMode.Automatic, null);

    public static FrameRefreshPolicy Unlimited { get; } = new(FrameRefreshMode.Unlimited, null);

    public static IReadOnlyList<int> SupportedFixedFramesPerSecond => SupportedFixedValuesView;

    public static FrameRefreshPolicy Fixed(int framesPerSecond)
    {
        if (Array.IndexOf(SupportedFixedValues, framesPerSecond) < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        return new FrameRefreshPolicy(FrameRefreshMode.Fixed, framesPerSecond);
    }

    public int? EffectiveMaximum(int? remoteMaximum) => Mode switch
    {
        FrameRefreshMode.Unlimited => null,
        FrameRefreshMode.Automatic => remoteMaximum,
        _ => remoteMaximum is { } maximum
            ? Math.Min(FixedFramesPerSecond!.Value, maximum)
            : FixedFramesPerSecond,
    };
}
