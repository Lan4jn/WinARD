namespace WinARD.Domain.Security;

public sealed record CredentialReference
{
    public CredentialReference(string store, string key)
    {
        Store = RequiredTrimmed(store, nameof(store));
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
}
