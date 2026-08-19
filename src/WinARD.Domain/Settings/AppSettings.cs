namespace WinARD.Domain.Settings;

public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

public enum SafeDiagnosticLevel
{
    Minimal = 0,
    Standard = 1,
    Verbose = 2,
}

public enum CredentialBackend
{
    AskEveryTime = 0,
    Windows = 1,
    EncryptedVault = 2,
}

public sealed record AppSettings
{
    public const int CurrentVersion = 1;
    public static readonly TimeSpan MinimumVaultIdleTimeout = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumVaultIdleTimeout = TimeSpan.FromHours(24);

    private AppSettings(
        int version,
        AppTheme theme,
        SafeDiagnosticLevel diagnosticLevel,
        bool clipboardEnabledByDefault,
        CredentialBackend defaultCredentialBackend,
        TimeSpan vaultIdleTimeout)
    {
        Version = version;
        Theme = theme;
        DiagnosticLevel = diagnosticLevel;
        ClipboardEnabledByDefault = clipboardEnabledByDefault;
        DefaultCredentialBackend = defaultCredentialBackend;
        VaultIdleTimeout = vaultIdleTimeout;
    }

    public static AppSettings Default { get; } = Create(
        CurrentVersion,
        AppTheme.System,
        SafeDiagnosticLevel.Standard,
        clipboardEnabledByDefault: true,
        CredentialBackend.Windows,
        TimeSpan.FromMinutes(15));

    public int Version { get; }
    public AppTheme Theme { get; }
    public SafeDiagnosticLevel DiagnosticLevel { get; }
    public bool ClipboardEnabledByDefault { get; }
    public CredentialBackend DefaultCredentialBackend { get; }
    public TimeSpan VaultIdleTimeout { get; }

    public static AppSettings Create(
        int version,
        AppTheme theme,
        SafeDiagnosticLevel diagnosticLevel,
        bool clipboardEnabledByDefault,
        CredentialBackend defaultCredentialBackend,
        TimeSpan vaultIdleTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(version, CurrentVersion);
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme));
        }
        if (!Enum.IsDefined(diagnosticLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticLevel));
        }
        if (!Enum.IsDefined(defaultCredentialBackend))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultCredentialBackend));
        }
        if (vaultIdleTimeout < MinimumVaultIdleTimeout ||
            vaultIdleTimeout > MaximumVaultIdleTimeout ||
            vaultIdleTimeout.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vaultIdleTimeout));
        }

        return new AppSettings(
            version,
            theme,
            diagnosticLevel,
            clipboardEnabledByDefault,
            defaultCredentialBackend,
            vaultIdleTimeout);
    }
}

public static class SafeDiagnosticLevelPolicy
{
    public static bool CapturesClipboardContent(this SafeDiagnosticLevel level)
    {
        EnsureDefined(level);
        return false;
    }

    public static bool CapturesRemotePixels(this SafeDiagnosticLevel level)
    {
        EnsureDefined(level);
        return false;
    }

    public static bool CapturesSecretMaterial(this SafeDiagnosticLevel level)
    {
        EnsureDefined(level);
        return false;
    }

    private static void EnsureDefined(SafeDiagnosticLevel level)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }
    }
}
