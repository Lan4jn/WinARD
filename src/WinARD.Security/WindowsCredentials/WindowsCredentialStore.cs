using System.Security.Cryptography;
using WinARD.Application.Ports;
using WinARD.Domain.Security;
using WinARD.Security.Secrets;

namespace WinARD.Security.WindowsCredentials;

public sealed class WindowsCredentialStore : ICredentialStore
{
    internal const int MaximumBlobBytes = 2560;

    private readonly IWindowsCredentialApi _native;

    public WindowsCredentialStore()
        : this(new WindowsCredentialApi())
    {
    }

    internal WindowsCredentialStore(IWindowsCredentialApi native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public ValueTask SaveAsync(
        CredentialReference reference,
        ISecret secret,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(secret);
        cancellationToken.ThrowIfCancellationRequested();
        if (secret.Length > MaximumBlobBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                "Windows Credential Manager limits generic credential blobs to 2560 bytes.");
        }

        var value = new byte[secret.Length];
        try
        {
            secret.CopyTo(value);
            _native.Write(TargetName(reference), value);
            return ValueTask.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    public ValueTask<ISecret?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var value = _native.Read(TargetName(reference));
        if (value is null)
        {
            return ValueTask.FromResult<ISecret?>(null);
        }

        try
        {
            return ValueTask.FromResult<ISecret?>(SecretBuffer.CopyFrom(value));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    public ValueTask DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        _native.Delete(TargetName(reference));
        return ValueTask.CompletedTask;
    }

    internal static string TargetName(CredentialReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return $"WinARD/{Uri.EscapeDataString(reference.Store)}/{Uri.EscapeDataString(reference.Key)}";
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows Credential Manager is only available on Windows.");
        }
    }
}
