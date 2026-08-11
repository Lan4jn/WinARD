namespace WinARD.Desktop.ViewModels;

internal enum QualityInputActivityKind
{
    Keyboard,
    Pointer,
    Scroll,
}

/// <summary>
/// A content-free input activity snapshot. Active flags remain set until a caller explicitly records them as false.
/// </summary>
internal sealed record QualityActivitySnapshot(
    DateTimeOffset? LastInputTimestamp,
    QualityInputActivityKind? LastInputKind,
    bool PointerDragActive,
    bool ScrollActive,
    int PendingInputCount)
{
    public static QualityActivitySnapshot Empty { get; } = new(null, null, false, false, 0);
}
