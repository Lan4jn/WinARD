using WinARD.Remote.Protocol.Handshake;

namespace WinARD.ProtocolProbe;

public sealed record ProbeResult(
    RfbVersion Version,
    RfbSecurityType SecurityType,
    ProbeCapture? Capture = null);

public sealed record ProbeCapture(string Path, int Width, int Height);
