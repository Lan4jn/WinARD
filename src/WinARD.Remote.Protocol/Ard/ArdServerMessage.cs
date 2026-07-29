namespace WinARD.Remote.Protocol.Ard;

public static class ArdServerMessage
{
    public const byte StateChangeType = 0x14;

    public static bool IsZeroPayloadControl(byte messageType) => messageType is 0x04 or 0x07;
}
