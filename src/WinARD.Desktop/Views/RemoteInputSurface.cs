using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace WinARD.Desktop.Views;

public sealed class RemoteInputSurface : Button, IDisposable
{
    internal const uint TransparentCursorResourceId = 101;
    internal const string CursorResourceModuleName = "WinARD.CursorResources.dll";

    private readonly InputCursor _defaultCursor =
        InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private readonly InputCursor _transparentCursor;
    private bool _disposed;

    public RemoteInputSurface()
    {
        _transparentCursor = InputDesktopResourceCursor.CreateFromModule(
            Path.Combine(AppContext.BaseDirectory, CursorResourceModuleName),
            TransparentCursorResourceId);
        ProtectedCursor = _defaultCursor;
    }

    public bool IsHostCursorHidden { get; private set; }

    internal InputCursor? CurrentHostCursor => ProtectedCursor;

    public void SetHostCursorHidden(bool hidden)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsHostCursorHidden = hidden;
        ProtectedCursor = hidden ? _transparentCursor : _defaultCursor;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ProtectedCursor = null;
        _transparentCursor.Dispose();
        _defaultCursor.Dispose();
    }
}
