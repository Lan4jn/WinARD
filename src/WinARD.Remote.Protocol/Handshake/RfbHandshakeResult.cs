namespace WinARD.Remote.Protocol.Handshake;

public sealed record RfbHandshakeResult(RfbVersion Version, RfbSecurityType SecurityType);
