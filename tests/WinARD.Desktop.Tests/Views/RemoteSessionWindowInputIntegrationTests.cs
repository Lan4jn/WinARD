using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Views;

public sealed class RemoteSessionWindowInputIntegrationTests
{
    [Fact]
    public void Remote_window_captures_button_handled_key_down_and_key_up()
    {
        var source = File.ReadAllText(RepositoryFile(
                "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"))
            .ReplaceLineEndings("\n");

        Assert.Contains(
            "InputSurface.AddHandler(\n            UIElement.KeyDownEvent,\n" +
            "            _keyDownHandler,\n            handledEventsToo: true)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "InputSurface.AddHandler(\n            UIElement.KeyUpEvent,\n" +
            "            _keyUpHandler,\n            handledEventsToo: true)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("InputSurface.KeyDown +=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("InputSurface.KeyUp +=", source, StringComparison.Ordinal);
        Assert.Contains(
            "InputSurface.RemoveHandler(UIElement.KeyDownEvent",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "InputSurface.RemoveHandler(UIElement.KeyUpEvent",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_window_routes_pointer_events_through_frame_surface()
    {
        var source = File.ReadAllText(RepositoryFile(
                "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"))
            .ReplaceLineEndings("\n");
        var xaml = File.ReadAllText(RepositoryFile(
                "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml"))
            .ReplaceLineEndings("\n");
        var events = new (string EventName, string HandlerName)[]
        {
            ("PointerPressed", "_pointerPressedHandler"),
            ("PointerMoved", "_pointerMovedHandler"),
            ("PointerReleased", "_pointerReleasedHandler"),
            ("PointerCanceled", "_pointerCanceledHandler"),
            ("PointerWheelChanged", "_pointerWheelChangedHandler"),
        };

        foreach (var (eventName, handlerName) in events)
        {
            Assert.Contains(
                $"FrameSurface.AddHandler(\n" +
                $"            UIElement.{eventName}Event,\n" +
                $"            {handlerName},\n" +
                "            handledEventsToo: true);",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                $"FrameSurface.RemoveHandler(UIElement.{eventName}Event, {handlerName});",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"InputSurface.{eventName} +=",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"InputSurface.AddHandler(\n            UIElement.{eventName}Event",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"InputSurface.RemoveHandler(UIElement.{eventName}Event",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"ViewportHost.AddHandler(\n            UIElement.{eventName}Event",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"ViewportHost.RemoveHandler(UIElement.{eventName}Event",
                source,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "FrameSurface.CapturePointer(args.Pointer)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FrameSurface.ReleasePointerCapture(args.Pointer)",
            source,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            source.Split(
                "args.GetCurrentPoint(FrameSurface).Properties",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "args.GetCurrentPoint(ViewportHost).Position",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "InputSurface.CapturePointer(args.Pointer)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "InputSurface.ReleasePointerCapture(args.Pointer)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"FrameSurface\"\n                    Background=\"Transparent\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_window_records_safe_keyboard_and_pointer_input_boundaries()
    {
        var source = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

        Assert.Contains(
            "new RemoteInputDiagnosticTracker(diagnosticSink)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RemoteInputDropReason.SessionClosing",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RemoteInputDropReason.InvalidTransform",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RemoteInputBoundary.UiCaptured",
            source,
            StringComparison.Ordinal);
        Assert.Contains("RecordKeyboardCaptured()", source, StringComparison.Ordinal);
        Assert.Contains("RecordPointerCaptured()", source, StringComparison.Ordinal);
        Assert.Contains("RecordKeyboardDropped(", source, StringComparison.Ordinal);
        Assert.Contains("RecordPointerDropped(", source, StringComparison.Ordinal);
        Assert.Contains("private int _closingStarted;", source, StringComparison.Ordinal);
        Assert.Contains("IsInputClosing()", source, StringComparison.Ordinal);
        Assert.Contains(
            "Interlocked.Exchange(ref _closingStarted, 1)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("keysym", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Coordinate", source, StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinARD.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException("Could not locate repository root.")
            : Path.Combine([directory.FullName, .. segments]);
    }
}
