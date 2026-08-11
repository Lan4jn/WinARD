namespace WinARD.ProtocolProbe.EncodingResearch;

public sealed record EncodingPrefixCapture
{
    private readonly byte[] _payloadPrefix;

    public EncodingPrefixCapture(
        int schemaVersion,
        int encodingId,
        CapturedRectangle rectangle,
        int prefixLength,
        string payloadSha256,
        byte[] payloadPrefix)
    {
        ArgumentNullException.ThrowIfNull(payloadSha256);
        ArgumentNullException.ThrowIfNull(payloadPrefix);
        SchemaVersion = schemaVersion;
        EncodingId = encodingId;
        Rectangle = rectangle;
        PrefixLength = prefixLength;
        PayloadSha256 = payloadSha256;
        _payloadPrefix = payloadPrefix.ToArray();
    }

    public int SchemaVersion { get; init; }

    public int EncodingId { get; init; }

    public CapturedRectangle Rectangle { get; init; }

    public int PrefixLength { get; init; }

    public string PayloadSha256 { get; init; }

    public byte[] PayloadPrefix => _payloadPrefix.ToArray();
}

public readonly record struct CapturedRectangle(
    ushort X,
    ushort Y,
    ushort Width,
    ushort Height);
