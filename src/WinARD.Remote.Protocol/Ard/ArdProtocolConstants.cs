namespace WinARD.Remote.Protocol.Ard;

#pragma warning disable CA1711

[Flags]
public enum ArdClientInitFlags : byte
{
    Shared = 0x01,
    Select = 0x40,
    Enhanced = 0x80,
    Ard = Shared | Select | Enhanced,
}

#pragma warning restore CA1711

public enum ArdControlMode : byte
{
    Observe = 0,
    Shared = 1,
    Exclusive = 2,
}

internal static class ArdProtocolConstants
{
    internal const byte ViewerInfo = 0x21;
    internal const byte AutoFramebufferUpdate = 0x09;
    internal const byte SetMode = 0x0A;
    internal const byte SetDisplay = 0x0D;
    internal const byte SetEncryption = 0x12;
    internal const byte StateChange = ArdServerMessage.StateChangeType;
    internal const byte ServerMayControl = 0x02;
    internal const byte ServerSessionSelect = 0x04;
    internal const int DisplayInfoEncoding = 1101;
    internal const int SessionEncryptionEncoding = 1103;
    internal const int DisplayInfo2Encoding = 1105;
}
