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
    Send,
    Receive,
}

public enum ArdEncryptedPacketFailureStage
{
    OuterLength,
    TruncatedCiphertext,
    CbcDecrypt,
    PlaintextTooShort,
    PayloadLength,
    Padding,
    Integrity,
    StateCommit,
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
