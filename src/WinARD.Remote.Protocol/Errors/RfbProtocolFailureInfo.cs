namespace WinARD.Remote.Protocol.Errors;

public enum RfbProtocolFailureKind
{
    UnexpectedServerMessage = 0,
    UnsupportedEncoding = 1,
    TruncatedRead = 2,
    MalformedFramebufferUpdate = 3,
    MalformedClipboard = 4,
    DecoderFailure = 5,
    MalformedArdStateChange = 6,
    RemoteSessionClosed = 7,
    ArdEncryptionNegotiation = 8,
    ArdEncryptionPacket = 9,
    ArdEncryptionIntegrity = 10,
}

public enum RfbProtocolReadStage
{
    ServerMessageType = 0,
    FramebufferHeader = 1,
    FramebufferRectangleHeader = 2,
    FramebufferRectanglePayload = 3,
    ClipboardHeader = 4,
    ClipboardPayload = 5,
    ArdStateChangeHeader = 6,
    ArdStateChangePayload = 7,
}

public enum ArdEncryptedPacketDirection
{
    Send = 0,
    Receive = 1,
}

public enum ArdEncryptedPacketFailureStage
{
    OuterLength = 0,
    TruncatedCiphertext = 1,
    CbcDecrypt = 2,
    PlaintextTooShort = 3,
    PayloadLength = 4,
    Padding = 5,
    Integrity = 6,
    StateCommit = 7,
}

public sealed record RfbProtocolFailureInfo(
    RfbProtocolFailureKind Kind,
    RfbProtocolReadStage? ReadStage = null,
    byte? ServerMessageType = null,
    int? EncodingId = null,
    int? RectangleIndex = null,
    ArdEncryptedPacketFailureStage? ArdEncryptionStage = null,
    ArdEncryptedPacketDirection? ArdEncryptionDirection = null,
    uint? ArdEncryptionSequence = null,
    int? ArdCiphertextLength = null)
{
    public RfbProtocolFailureInfo(
        RfbProtocolFailureKind Kind,
        RfbProtocolReadStage? ReadStage,
        byte? ServerMessageType,
        int? EncodingId,
        int? RectangleIndex)
        : this(
            Kind,
            ReadStage,
            ServerMessageType,
            EncodingId,
            RectangleIndex,
            null,
            null,
            null,
            null)
    {
    }

    public void Deconstruct(
        out RfbProtocolFailureKind Kind,
        out RfbProtocolReadStage? ReadStage,
        out byte? ServerMessageType,
        out int? EncodingId,
        out int? RectangleIndex)
    {
        Kind = this.Kind;
        ReadStage = this.ReadStage;
        ServerMessageType = this.ServerMessageType;
        EncodingId = this.EncodingId;
        RectangleIndex = this.RectangleIndex;
    }

    internal RfbProtocolFailureInfo FillMissingFrom(RfbProtocolFailureInfo outer)
    {
        ArgumentNullException.ThrowIfNull(outer);

        return this with
        {
            ReadStage = ReadStage ?? outer.ReadStage,
            ServerMessageType = ServerMessageType ?? outer.ServerMessageType,
            EncodingId = EncodingId ?? outer.EncodingId,
            RectangleIndex = RectangleIndex ?? outer.RectangleIndex,
            ArdEncryptionStage = ArdEncryptionStage ?? outer.ArdEncryptionStage,
            ArdEncryptionDirection = ArdEncryptionDirection ?? outer.ArdEncryptionDirection,
            ArdEncryptionSequence = ArdEncryptionSequence ?? outer.ArdEncryptionSequence,
            ArdCiphertextLength = ArdCiphertextLength ?? outer.ArdCiphertextLength,
        };
    }
}
