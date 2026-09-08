using System.Text.Json.Serialization;
using WinARD.ProtocolProbe.EncodingResearch;

namespace WinARD.ProtocolProbe.EncodingResearch;

public enum MvsRecordCompleteness
{
    PrefixOnly = 0,
    RecordCompleteUnderLengthHypothesis = 1,
    SuccessorBoundaryValidated = 2,
}

public enum MvsRecordKind
{
    ControlSetup = 0,
    ImageSlice = 1,
    Unknown = 2,
}

public sealed record MvsSampleCapture(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("encodingId")] int EncodingId,
    [property: JsonPropertyName("sampleName")] string SampleName,
    [property: JsonPropertyName("rectangle")] CapturedRectangle Rectangle,
    [property: JsonPropertyName("prefixLength")] int PrefixLength,
    [property: JsonPropertyName("payloadSha256")] string PayloadSha256,
    [property: JsonIgnore] byte[] PayloadPrefix,
    [property: JsonPropertyName("heuristicBigEndianLength")] uint? HeuristicBigEndianLength,
    [property: JsonPropertyName("heuristicSignature")] string HeuristicSignature,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("completeness")] MvsRecordCompleteness Completeness = MvsRecordCompleteness.PrefixOnly,
    [property: JsonPropertyName("recordKind")] MvsRecordKind RecordKind = MvsRecordKind.Unknown,
    [property: JsonPropertyName("declaredLength")] uint? DeclaredLength = null,
    [property: JsonPropertyName("actualLength")] int? ActualLength = null,
    [property: JsonPropertyName("recordIndex")] int RecordIndex = 0,
    [property: JsonPropertyName("updateIndex")] int UpdateIndex = 0,
    [property: JsonPropertyName("successorMessageType")] byte? SuccessorMessageType = null,
    [property: JsonPropertyName("successorEncodingId")] int? SuccessorEncodingId = null,
    [property: JsonPropertyName("incomplete")] bool Incomplete = false)
{
    [JsonIgnore]
    public bool IsIncomplete => Incomplete || Completeness == MvsRecordCompleteness.PrefixOnly;
}
