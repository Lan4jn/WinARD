namespace WinARD.Domain.Security;

public sealed record CredentialReference
{
    public CredentialReference(string store, string key)
    {
        Store = ValidStore(store);
        Key = RequiredTrimmed(key, nameof(key));
    }

    public string Store { get; }

    public string Key { get; }

    public static CredentialReference Create(string store, string key) => new(store, key);

    public override string ToString() => $"credential://{Uri.EscapeDataString(Store)}/{Uri.EscapeDataString(Key)}";

    private static string RequiredTrimmed(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be blank.", parameterName);
        }

        return value.Trim();
    }

    private static string ValidStore(string store)
    {
        var normalizedStore = RequiredTrimmed(store, nameof(store));
        if (!normalizedStore.All(static character =>
                character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-') ||
            !Uri.TryCreate($"credential://{normalizedStore}/reference", UriKind.Absolute, out _))
        {
            throw new ArgumentException("Store must be a valid URI authority token.", nameof(store));
        }

        return normalizedStore;
    }
}
