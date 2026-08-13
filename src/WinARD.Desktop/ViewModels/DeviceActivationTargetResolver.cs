namespace WinARD.Desktop.ViewModels;

public static class DeviceActivationTargetResolver
{
    public static DeviceItemViewModel? Resolve(object? dataContext) =>
        dataContext as DeviceItemViewModel;
}
