namespace WinARD.Desktop.ViewModels;

public sealed record DeviceDetailsPresentation(
    string Username,
    string NetworkPath,
    string PreviewPlaceholder)
{
    public static DeviceDetailsPresentation From(DeviceItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Profile is not { } profile)
        {
            return new("未设置", $"Bonjour {item.Endpoint}", "无预览");
        }

        var path = profile.SshProfile is { } ssh
            ? $"SSH {ssh.Host}:{ssh.Port} → {ssh.TargetHost}:{ssh.TargetPort}"
            : $"直接连接 {profile.Host}:{profile.Port}";
        return new(profile.MacUsername, path, "无预览");
    }
}
