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
    MalformedHandshake = 11,
}

public enum RfbDecoderFailureReason
{
    Unknown = 0,
    InvalidCompressedStream = 1,
    MissingSyncFlushBoundary = 2,
    DecompressedLengthMismatch = 3,
    OutputLimitExceeded = 4,
    CompletedStreamOrTrailingBytes = 5,
    IncompleteCompressedSegment = 6,
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

public enum RfbHandshakeStage
{
    VersionBanner = 0,
    VersionParse = 1,
    SecurityType33 = 2,
    SecurityTypeCount = 3,
    SecurityTypes = 4,
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
    int? ArdCiphertextLength = null,
    RfbHandshakeStage? HandshakeStage = null,
    int? ExpectedByteCount = null,
    int? ActualByteCount = null,
    RfbDecoderFailureReason? DecoderFailureReason = null)
{
    public RfbProtocolFailureInfo(
        RfbProtocolFailureKind Kind,
        RfbProtocolReadStage? ReadStage,
        byte? ServerMessageType,
        int? EncodingId,
        int? RectangleIndex,
        ArdEncryptedPacketFailureStage? ArdEncryptionStage,
        ArdEncryptedPacketDirection? ArdEncryptionDirection,
        uint? ArdEncryptionSequence,
        int? ArdCiphertextLength,
        RfbHandshakeStage? HandshakeStage,
        int? ExpectedByteCount,
        int? ActualByteCount)
        : this(
            Kind,
            ReadStage,
            ServerMessageType,
            EncodingId,
            RectangleIndex,
            ArdEncryptionStage,
            ArdEncryptionDirection,
            ArdEncryptionSequence,
            ArdCiphertextLength,
            HandshakeStage,
            ExpectedByteCount,
            ActualByteCount,
            null)
    {
    }

    public RfbProtocolFailureInfo(
        RfbProtocolFailureKind Kind,
        RfbProtocolReadStage? ReadStage,
        byte? ServerMessageType,
        int? EncodingId,
        int? RectangleIndex,
        ArdEncryptedPacketFailureStage? ArdEncryptionStage,
        ArdEncryptedPacketDirection? ArdEncryptionDirection,
        uint? ArdEncryptionSequence,
        int? ArdCiphertextLength)
        : this(
            Kind,
            ReadStage,
            ServerMessageType,
            EncodingId,
            RectangleIndex,
            ArdEncryptionStage,
            ArdEncryptionDirection,
            ArdEncryptionSequence,
            ArdCiphertextLength,
            null,
            null,
            null,
            null)
    {
    }

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
            null,
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

    public void Deconstruct(
        out RfbProtocolFailureKind Kind,
        out RfbProtocolReadStage? ReadStage,
        out byte? ServerMessageType,
        out int? EncodingId,
        out int? RectangleIndex,
        out ArdEncryptedPacketFailureStage? ArdEncryptionStage,
        out ArdEncryptedPacketDirection? ArdEncryptionDirection,
        out uint? ArdEncryptionSequence,
        out int? ArdCiphertextLength)
    {
        Kind = this.Kind;
        ReadStage = this.ReadStage;
        ServerMessageType = this.ServerMessageType;
        EncodingId = this.EncodingId;
        RectangleIndex = this.RectangleIndex;
        ArdEncryptionStage = this.ArdEncryptionStage;
        ArdEncryptionDirection = this.ArdEncryptionDirection;
        ArdEncryptionSequence = this.ArdEncryptionSequence;
        ArdCiphertextLength = this.ArdCiphertextLength;
    }

    public void Deconstruct(
        out RfbProtocolFailureKind Kind,
        out RfbProtocolReadStage? ReadStage,
        out byte? ServerMessageType,
        out int? EncodingId,
        out int? RectangleIndex,
        out ArdEncryptedPacketFailureStage? ArdEncryptionStage,
        out ArdEncryptedPacketDirection? ArdEncryptionDirection,
        out uint? ArdEncryptionSequence,
        out int? ArdCiphertextLength,
        out RfbHandshakeStage? HandshakeStage,
        out int? ExpectedByteCount,
        out int? ActualByteCount)
    {
        Kind = this.Kind;
        ReadStage = this.ReadStage;
        ServerMessageType = this.ServerMessageType;
        EncodingId = this.EncodingId;
        RectangleIndex = this.RectangleIndex;
        ArdEncryptionStage = this.ArdEncryptionStage;
        ArdEncryptionDirection = this.ArdEncryptionDirection;
        ArdEncryptionSequence = this.ArdEncryptionSequence;
        ArdCiphertextLength = this.ArdCiphertextLength;
        HandshakeStage = this.HandshakeStage;
        ExpectedByteCount = this.ExpectedByteCount;
        ActualByteCount = this.ActualByteCount;
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
            HandshakeStage = HandshakeStage ?? outer.HandshakeStage,
            ExpectedByteCount = ExpectedByteCount ?? outer.ExpectedByteCount,
            ActualByteCount = ActualByteCount ?? outer.ActualByteCount,
            DecoderFailureReason = DecoderFailureReason ?? outer.DecoderFailureReason,
        };
    }
}
