namespace WinARD.ProtocolProbe.RdmCapture;

public sealed record RdmCaptureReport
{
    public RdmCaptureReport(
        int schemaVersion,
        string profile,
        string clientVersion,
        byte clientInit,
        CapturedPixelFormat? pixelFormat,
        IReadOnlyList<int> encodings,
        IReadOnlyList<CapturedClientMessage> messages,
        bool reachedFramebufferRequest,
        byte? stoppedAtUnknownMessageType)
    {
        ArgumentNullException.ThrowIfNull(encodings);
        ArgumentNullException.ThrowIfNull(messages);

        SchemaVersion = schemaVersion;
        Profile = profile;
        ClientVersion = clientVersion;
        ClientInit = clientInit;
        PixelFormat = pixelFormat;
        Encodings = Array.AsReadOnly(encodings.ToArray());
        Messages = Array.AsReadOnly(messages.ToArray());
        ReachedFramebufferRequest = reachedFramebufferRequest;
        StoppedAtUnknownMessageType = stoppedAtUnknownMessageType;
    }

    public int SchemaVersion { get; }
    public string Profile { get; }
    public string ClientVersion { get; }
    public byte ClientInit { get; }
    public CapturedPixelFormat? PixelFormat { get; }
    public IReadOnlyList<int> Encodings { get; }
    public IReadOnlyList<CapturedClientMessage> Messages { get; }
    public bool ReachedFramebufferRequest { get; }
    public byte? StoppedAtUnknownMessageType { get; }
}

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
