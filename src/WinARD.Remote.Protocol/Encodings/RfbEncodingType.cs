namespace WinARD.Remote.Protocol.Encodings;

public enum RfbEncodingType
{
    Raw = 0,
    CopyRect = 1,
    Zrle = 16,
    ArdDisplayInfo = 1101,
    ArdSessionEncryption = 1103,
    ArdDisplayInfo2 = 1105,
    DesktopSize = -223,
    Cursor = -239,
}
