namespace WinARD.Remote.Protocol.Ard;

public static class ArdServerMessage
{
    public static bool IsZeroPayloadControl(byte messageType) => messageType is 0x04 or 0x07;
}
