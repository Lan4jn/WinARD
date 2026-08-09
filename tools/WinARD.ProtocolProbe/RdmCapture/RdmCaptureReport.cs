namespace WinARD.ProtocolProbe.RdmCapture;

public sealed record RdmCaptureReport(
    int SchemaVersion,
    string Profile,
    string ClientVersion,
    byte ClientInit,
    CapturedPixelFormat? PixelFormat,
    IReadOnlyList<int> Encodings,
    IReadOnlyList<CapturedClientMessage> Messages,
    bool ReachedFramebufferRequest,
    byte? StoppedAtUnknownMessageType);

public sealed record CapturedPixelFormat(
    byte BitsPerPixel,
    byte Depth,
    bool BigEndian,
    bool TrueColor,
    ushort RedMax,
    ushort GreenMax,
    ushort BlueMax,
    byte RedShift,
    byte GreenShift,
    byte BlueShift);

public sealed record CapturedClientMessage(
    byte Type,
    string Name,
    int WireLength,
    string? NumericPayloadHex,
    string? PayloadSha256);
