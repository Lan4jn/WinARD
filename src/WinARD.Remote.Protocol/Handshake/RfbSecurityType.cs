namespace WinARD.Remote.Protocol.Handshake;

public enum RfbSecurityType : byte
{
    Invalid = 0,
    None = 1,
    VncAuthentication = 2,
    AppleRemoteDesktop = 30,
}
