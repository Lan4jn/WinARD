using System.Collections.ObjectModel;
using WinARD.Domain.Connections;

namespace WinARD.Desktop.ViewModels;

public sealed record FrameRefreshOption(
    FrameRefreshPolicy Policy,
    string DisplayName,
    bool IsEnabled,
    string? ConstraintText);

internal static class FrameRefreshOptions
{
    private static readonly (FrameRefreshPolicy Policy, string DisplayName)[] FixedOptions =
    [
        (FrameRefreshPolicy.Fixed(30), "30"),
        (FrameRefreshPolicy.Fixed(45), "45"),
        (FrameRefreshPolicy.Fixed(60), "60"),
        (FrameRefreshPolicy.Fixed(75), "75"),
        (FrameRefreshPolicy.Fixed(90), "90"),
        (FrameRefreshPolicy.Fixed(105), "105"),
        (FrameRefreshPolicy.Fixed(120), "120"),
    ];

    public static IReadOnlyList<FrameRefreshOption> Create(
        FrameRefreshPolicy selectedPolicy,
        int? remoteMaximum)
    {
        var options = new List<FrameRefreshOption>
        {
            CreateOption(FrameRefreshPolicy.Automatic, "自动", selectedPolicy, remoteMaximum),
        };
        options.AddRange(FixedOptions.Select(option =>
            CreateOption(option.Policy, option.DisplayName, selectedPolicy, remoteMaximum)));
        options.Add(CreateOption(FrameRefreshPolicy.Unlimited, "无限", selectedPolicy, remoteMaximum));
        return new ReadOnlyCollection<FrameRefreshOption>(options);
    }

    private static FrameRefreshOption CreateOption(
        FrameRefreshPolicy policy,
        string displayName,
        FrameRefreshPolicy selectedPolicy,
        int? remoteMaximum)
    {
        var exceedsMaximum = policy.Mode == FrameRefreshMode.Fixed &&
            remoteMaximum is { } maximum &&
            policy.FixedFramesPerSecond > maximum;
        var constraint = exceedsMaximum
            ? policy == selectedPolicy
                ? $"当前上限 {remoteMaximum}，有效 {policy.EffectiveMaximum(remoteMaximum)}"
                : $"当前上限 {remoteMaximum}"
            : null;
        return new FrameRefreshOption(policy, displayName, !exceedsMaximum, constraint);
    }
}

internal static class FrameRefreshOptionPresentation
{
    public static string AutomationName(FrameRefreshOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var mode = option.Policy.Mode switch
        {
            FrameRefreshMode.Automatic => "自动刷新",
            FrameRefreshMode.Fixed => $"固定 {option.Policy.FixedFramesPerSecond} FPS",
            FrameRefreshMode.Unlimited => "无限刷新",
            _ => "未知刷新模式",
        };
        return option.ConstraintText is { } constraint
            ? $"{mode}，{constraint}"
            : mode;
    }
}
