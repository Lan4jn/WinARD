namespace WinARD.Remote.Protocol.Ard;

public enum ArdStateChangeStatus : ushort
{
    LocalUserClosed = 1,
    PasteboardChanged = 2,
    PasteboardDataNeeded = 3,
    Tickle = 4,
    Sleep = 5,
    Wake = 6,
    CursorHidden = 11,
    CursorVisible = 12,
}

public sealed record ArdStateChange(
    byte Padding,
    ushort PayloadSize,
    ushort Flags,
    ushort Status,
    int ExtraPayloadLength);
