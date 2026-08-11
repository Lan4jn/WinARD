namespace WinARD.ProtocolProbe.EncodingResearch;

public sealed record EncodingPrefixCapture(
    int SchemaVersion,
    int EncodingId,
    CapturedRectangle Rectangle,
    int PrefixLength,
    string PayloadSha256,
    byte[] PayloadPrefix);

public readonly record struct CapturedRectangle(
    ushort X,
    ushort Y,
    ushort Width,
    ushort Height);
