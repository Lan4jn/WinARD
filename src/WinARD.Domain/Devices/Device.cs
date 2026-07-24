namespace WinARD.Domain.Devices;

public sealed record Device
{
    private Device(Guid id, string displayName, DateTimeOffset createdUtc, DateTimeOffset updatedUtc)
    {
        Id = id;
        DisplayName = displayName;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
    }

    public Guid Id { get; }

    public string DisplayName { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset UpdatedUtc { get; }

    public static Device Create(Guid id, string displayName, DateTimeOffset createdUtc, DateTimeOffset updatedUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Device ID cannot be empty.", nameof(id));
        }

        if (updatedUtc < createdUtc)
        {
            throw new ArgumentException("Updated time cannot be earlier than created time.", nameof(updatedUtc));
        }

        return new Device(id, RequiredTrimmed(displayName, nameof(displayName)), createdUtc, updatedUtc);
    }

    private static string RequiredTrimmed(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be blank.", parameterName);
        }

        return value.Trim();
    }
}
