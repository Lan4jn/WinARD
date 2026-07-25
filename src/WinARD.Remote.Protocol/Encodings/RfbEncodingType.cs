namespace WinARD.Remote.Protocol.Encodings;

public enum RfbEncodingType
{
    Raw = 0,
    CopyRect = 1,
    Zrle = 16,
    DesktopSize = -223,
    Cursor = -239,
}
