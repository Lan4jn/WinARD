namespace WinARD.Infrastructure.Database;

public sealed class UnsupportedSchemaVersionException : InvalidOperationException
{
    public UnsupportedSchemaVersionException(int actual, int supported)
        : base($"Database schema version {actual} is newer than supported version {supported}.")
    {
        ActualVersion = actual;
        SupportedVersion = supported;
    }

    public int ActualVersion { get; }

    public int SupportedVersion { get; }
}
