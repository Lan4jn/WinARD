namespace WinARD.Transport.Ssh;

public sealed record OpenSshExecutablePaths(
    string SshPath,
    string KeyScanPath);

public interface IOpenSshExecutableResolver
{
    OpenSshExecutablePaths Resolve();
}

public enum OpenSshExecutableKind
{
    Ssh,
    KeyScan,
}

public sealed class WindowsOpenSshExecutableResolver
    : IOpenSshExecutableResolver
{
    private readonly string _windowsDirectory;
    private readonly string? _configuredSshPath;
    private readonly string? _configuredKeyScanPath;
    private readonly Func<string, bool> _fileExists;

    public WindowsOpenSshExecutableResolver(
        string? configuredSshPath = null,
        string? configuredKeyScanPath = null)
        : this(
            Environment.GetEnvironmentVariable("WINDIR") ??
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            configuredSshPath,
            configuredKeyScanPath,
            File.Exists)
    {
    }

    internal WindowsOpenSshExecutableResolver(
        string windowsDirectory,
        string? configuredSshPath,
        string? configuredKeyScanPath,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            throw new ArgumentException(
                "The Windows directory cannot be blank.",
                nameof(windowsDirectory));
        }

        _windowsDirectory = Path.GetFullPath(windowsDirectory);
        _configuredSshPath = configuredSshPath;
        _configuredKeyScanPath = configuredKeyScanPath;
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    }

    public OpenSshExecutablePaths Resolve()
    {
        var openSshDirectory = Path.Combine(
            _windowsDirectory,
            "System32",
            "OpenSSH");
        return new OpenSshExecutablePaths(
            ResolveExecutable(
                OpenSshExecutableKind.Ssh,
                _configuredSshPath,
                Path.Combine(openSshDirectory, "ssh.exe"),
                "ssh.exe"),
            ResolveExecutable(
                OpenSshExecutableKind.KeyScan,
                _configuredKeyScanPath,
                Path.Combine(openSshDirectory, "ssh-keyscan.exe"),
                "ssh-keyscan.exe"));
    }

    private string ResolveExecutable(
        OpenSshExecutableKind kind,
        string? configuredPath,
        string defaultPath,
        string expectedFileName)
    {
        var candidate = configuredPath is null
            ? defaultPath
            : ValidateConfiguredPath(configuredPath, expectedFileName);
        var fullPath = Path.GetFullPath(candidate);
        if (!_fileExists(fullPath))
        {
            throw new OpenSshExecutableNotFoundException(kind, fullPath);
        }

        return fullPath;
    }

    private static string ValidateConfiguredPath(
        string path,
        string expectedFileName)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            path.Any(char.IsControl))
        {
            throw new OpenSshExecutableConfigurationException(
                "Configured OpenSSH executable paths must be absolute and contain no control characters.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(
                Path.GetFileName(fullPath),
                expectedFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenSshExecutableConfigurationException(
                $"Configured OpenSSH executable must be named '{expectedFileName}'.");
        }

        return fullPath;
    }
}

public sealed class OpenSshExecutableNotFoundException : Exception
{
    public OpenSshExecutableNotFoundException(
        OpenSshExecutableKind executableKind,
        string expectedPath)
        : base($"The required OpenSSH executable was not found at '{expectedPath}'.")
    {
        ExecutableKind = executableKind;
        ExpectedPath = expectedPath;
    }

    public OpenSshExecutableKind ExecutableKind { get; }

    public string ExpectedPath { get; }
}

public sealed class OpenSshExecutableConfigurationException : Exception
{
    public OpenSshExecutableConfigurationException(string message)
        : base(message)
    {
    }
}
