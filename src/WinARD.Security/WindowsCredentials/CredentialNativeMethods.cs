using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WinARD.Security.WindowsCredentials;

internal interface IWindowsCredentialApi
{
    void Write(string target, byte[] secret);

    byte[]? Read(string target);

    void Delete(string target);
}

internal sealed class WindowsCredentialApi : IWindowsCredentialApi
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public void Write(string target, byte[] secret)
    {
        var blob = Marshal.AllocCoTaskMem(secret.Length);
        try
        {
            if (secret.Length > 0)
            {
                Marshal.Copy(secret, 0, blob, secret.Length);
            }

            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = checked((uint)secret.Length),
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredentialNativeMethods.CredWrite(
                ref credential,
                flags: 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            ZeroAndFree(blob, secret.Length);
        }
    }

    public byte[]? Read(string target)
    {
        if (!CredentialNativeMethods.CredRead(
            target,
            CredTypeGeneric,
            flags: 0,
            out var credential))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error);
        }

        using (credential)
        {
            var native = Marshal.PtrToStructure<NativeCredential>(
                credential.DangerousGetHandle());
            if (native.CredentialBlobSize > WindowsCredentialStore.MaximumBlobBytes)
            {
                throw new InvalidDataException(
                    "The Windows credential blob exceeds the supported limit.");
            }

            var value = new byte[native.CredentialBlobSize];
            try
            {
                if (value.Length > 0)
                {
                    Marshal.Copy(native.CredentialBlob, value, 0, value.Length);
                }

                return value;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(value);
                throw;
            }
            finally
            {
                ZeroMemory(native.CredentialBlob, value.Length);
            }
        }
    }

    public void Delete(string target)
    {
        if (CredentialNativeMethods.CredDelete(
            target,
            CredTypeGeneric,
            flags: 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error);
        }
    }

    private static void ZeroAndFree(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        ZeroMemory(pointer, length);
        Marshal.FreeCoTaskMem(pointer);
    }

    private static void ZeroMemory(IntPtr pointer, int length)
    {
        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NativeCredential
{
    public uint Flags;
    public uint Type;
    public string TargetName;
    public string? Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public uint CredentialBlobSize;
    public IntPtr CredentialBlob;
    public uint Persist;
    public uint AttributeCount;
    public IntPtr Attributes;
    public string? TargetAlias;
    public string UserName;
}

internal sealed class SafeCredentialHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeCredentialHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        CredentialNativeMethods.CredFree(handle);
        return true;
    }
}

internal static class CredentialNativeMethods
{
    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredWriteW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CredWrite(
        ref NativeCredential credential,
        uint flags);

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredReadW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out SafeCredentialHandle credential);

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredDeleteW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CredDelete(
        string target,
        uint type,
        uint flags);

    [DllImport("Advapi32.dll")]
    internal static extern void CredFree(IntPtr buffer);
}
