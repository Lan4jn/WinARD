namespace WinARD.Domain.Errors;

public sealed record WinArdError
{
    private WinArdError(ConnectionStage stage, string code, string userMessage, string correlationId)
    {
        Stage = stage;
        Code = code;
        UserMessage = userMessage;
        CorrelationId = correlationId;
    }

    public ConnectionStage Stage { get; }

    public string Code { get; }

    public string UserMessage { get; }

    public string CorrelationId { get; }

    public static WinArdError Create(ConnectionStage stage, string code, string userMessage, string correlationId) =>
        Enum.IsDefined(stage)
            ? new WinArdError(
                stage,
                RequiredTrimmed(code, nameof(code)),
                RequiredTrimmed(userMessage, nameof(userMessage)),
                RequiredTrimmed(correlationId, nameof(correlationId)))
            : throw new ArgumentOutOfRangeException(nameof(stage), "Connection stage must be defined.");

    private static string RequiredTrimmed(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be blank.", parameterName);
        }

        return value.Trim();
    }
}
