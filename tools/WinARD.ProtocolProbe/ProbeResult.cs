using WinARD.Remote.Protocol.Handshake;

namespace WinARD.ProtocolProbe;

public sealed record ProbeResult(RfbVersion Version, RfbSecurityType SecurityType);
