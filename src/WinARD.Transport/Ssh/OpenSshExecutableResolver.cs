using System.Runtime.InteropServices;

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
    private readonly IWindowsSystemDirectoryProvider _systemDirectoryProvider;
    private readonly string? _configuredSshPath;
    private readonly string? _configuredKeyScanPath;
    private readonly Func<string, bool> _fileExists;

    public WindowsOpenSshExecutableResolver(
        string? configuredSshPath = null,
        string? configuredKeyScanPath = null)
        : this(
            new WindowsSystemDirectoryProvider(),
            configuredSshPath,
            configuredKeyScanPath,
            File.Exists)
    {
    }

    internal WindowsOpenSshExecutableResolver(
        IWindowsSystemDirectoryProvider systemDirectoryProvider,
        string? configuredSshPath,
        string? configuredKeyScanPath,
        Func<string, bool> fileExists)
    {
        _systemDirectoryProvider = systemDirectoryProvider ??
            throw new ArgumentNullException(nameof(systemDirectoryProvider));
        _configuredSshPath = configuredSshPath;
        _configuredKeyScanPath = configuredKeyScanPath;
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    }

    public OpenSshExecutablePaths Resolve()
    {
        var needsSystemDirectory =
            _configuredSshPath is null ||
            _configuredKeyScanPath is null;
        var openSshDirectory = needsSystemDirectory
            ? Path.Combine(
                _systemDirectoryProvider.GetSystemDirectory(),
                "OpenSSH")
            : null;
        return new OpenSshExecutablePaths(
            ResolveExecutable(
                OpenSshExecutableKind.Ssh,
                _configuredSshPath,
                openSshDirectory is null
                    ? null
                    : Path.Combine(openSshDirectory, "ssh.exe"),
                "ssh.exe"),
            ResolveExecutable(
                OpenSshExecutableKind.KeyScan,
                _configuredKeyScanPath,
                openSshDirectory is null
                    ? null
                    : Path.Combine(openSshDirectory, "ssh-keyscan.exe"),
                "ssh-keyscan.exe"));
    }

    private string ResolveExecutable(
        OpenSshExecutableKind kind,
        string? configuredPath,
        string? defaultPath,
        string expectedFileName)
    {
        var candidate = configuredPath is null
            ? defaultPath ??
                throw new InvalidOperationException(
                    "A native OpenSSH system path was required but not resolved.")
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

public interface IWindowsSystemDirectoryProvider
{
    string GetSystemDirectory();
}

internal interface IWindowsSystemDirectoryNativeApi
{
    bool IsWindows { get; }

    uint GetSystemDirectory(char[] buffer, uint capacity);

    int GetLastError();
}

public sealed class WindowsSystemDirectoryProvider
    : IWindowsSystemDirectoryProvider
{
    private const int DefaultInitialBufferCapacity = 260;
    private const int DefaultMaximumBufferCapacity = 32768;
    private readonly IWindowsSystemDirectoryNativeApi _nativeApi;
    private readonly int _initialBufferCapacity;
    private readonly int _maximumBufferCapacity;

    public WindowsSystemDirectoryProvider()
        : this(
            new WindowsSystemDirectoryNativeApi(),
            DefaultInitialBufferCapacity,
            DefaultMaximumBufferCapacity)
    {
    }

    internal WindowsSystemDirectoryProvider(
        IWindowsSystemDirectoryNativeApi nativeApi,
        int initialBufferCapacity,
        int maximumBufferCapacity)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        if (initialBufferCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialBufferCapacity),
                "Initial buffer capacity must be positive.");
        }

        if (maximumBufferCapacity < initialBufferCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBufferCapacity),
                "Maximum buffer capacity must not be smaller than the initial capacity.");
        }

        _initialBufferCapacity = initialBufferCapacity;
        _maximumBufferCapacity = maximumBufferCapacity;
    }

    public string GetSystemDirectory()
    {
        if (!_nativeApi.IsWindows)
        {
            throw new OpenSshPlatformNotSupportedException();
        }

        var capacity = _initialBufferCapacity;
        while (true)
        {
            var buffer = new char[capacity];
            var result = _nativeApi.GetSystemDirectory(
                buffer,
                checked((uint)capacity));
            if (result == 0)
            {
                throw new OpenSshSystemDirectoryException(
                    _nativeApi.GetLastError());
            }

            if (result < capacity)
            {
                var path = new string(buffer, 0, checked((int)result));
                if (string.IsNullOrWhiteSpace(path) ||
                    !Path.IsPathFullyQualified(path) ||
                    path.Any(char.IsControl))
                {
                    throw new OpenSshSystemDirectoryException(
                        "GetSystemDirectoryW returned an invalid system directory.");
                }

                return Path.GetFullPath(path);
            }

            var requiredCapacity = result > int.MaxValue
                ? int.MaxValue
                : checked((int)result);
            if (requiredCapacity <= capacity)
            {
                requiredCapacity = checked(capacity * 2);
            }

            if (requiredCapacity > _maximumBufferCapacity)
            {
                throw new OpenSshSystemDirectoryException(
                    "GetSystemDirectoryW required a path buffer larger than the configured safety limit.");
            }

            capacity = requiredCapacity;
        }
    }
}

internal sealed class WindowsSystemDirectoryNativeApi
    : IWindowsSystemDirectoryNativeApi
{
    public bool IsWindows => OperatingSystem.IsWindows();

    public uint GetSystemDirectory(char[] buffer, uint capacity) =>
        GetSystemDirectoryW(buffer, capacity);

    public int GetLastError() => Marshal.GetLastWin32Error();

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetSystemDirectoryW",
        ExactSpelling = true,
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetSystemDirectoryW(
        [Out] char[] buffer,
        uint capacity);
}

public sealed class OpenSshSystemDirectoryException : Exception
{
    public OpenSshSystemDirectoryException(int nativeErrorCode)
        : base(
            $"GetSystemDirectoryW failed with Win32 error {nativeErrorCode}.")
    {
        NativeErrorCode = nativeErrorCode;
    }

    public OpenSshSystemDirectoryException(string message)
        : base(message)
    {
    }

    public int NativeErrorCode { get; }
}

public sealed class OpenSshPlatformNotSupportedException
    : PlatformNotSupportedException
{
    public OpenSshPlatformNotSupportedException()
        : base("System OpenSSH discovery is supported only on Windows.")
    {
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
