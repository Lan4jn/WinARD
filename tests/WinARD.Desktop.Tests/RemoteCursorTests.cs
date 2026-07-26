using System.Reflection;
using System.Reflection.PortableExecutable;
using Microsoft.UI.Input;
using WinARD.Desktop.Views;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests;

public sealed class RemoteCursorTests
{
    [Fact]
    public void Remote_input_surface_exposes_the_actual_current_input_cursor_for_verification()
    {
        var property = typeof(RemoteInputSurface).GetProperty(
            "CurrentHostCursor",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(property);
        Assert.Equal(typeof(InputCursor), property.PropertyType);
    }

    [Fact]
    public void Transparent_cursor_resource_module_is_packaged_with_native_resources()
    {
        var modulePath = Path.Combine(AppContext.BaseDirectory, "WinARD.CursorResources.dll");

        Assert.True(File.Exists(modulePath), $"Missing cursor resource module: {modulePath}");
        using var stream = File.OpenRead(modulePath);
        using var reader = new PEReader(stream);
        Assert.True(reader.PEHeaders.PEHeader?.ResourceTableDirectory.Size > 0);
    }
}
