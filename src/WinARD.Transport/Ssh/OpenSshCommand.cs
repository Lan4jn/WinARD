using System.Globalization;
using System.Net;
using WinARD.Domain.Connections;

namespace WinARD.Transport.Ssh;

public sealed record OpenSshProcessStart(
    string FileName,
    IReadOnlyList<string> Arguments);

internal static class OpenSshCommandBuilder
{
    public static OpenSshProcessStart BuildTunnel(
        string sshPath,
        SshProfile profile,
        string knownHostsPath)
    {
        ValidateExecutablePath(sshPath, "ssh.exe", nameof(sshPath));
        ArgumentNullException.ThrowIfNull(profile);
        ValidatePath(knownHostsPath, nameof(knownHostsPath));
        ValidateUsername(profile.Username);
        var sshEndpoint = new SshHostKeyEndpoint(profile.Host, profile.Port);
        var targetHost = CanonicalHost(profile.TargetHost, nameof(profile.TargetHost));
        var target = IPAddress.TryParse(targetHost, out var targetAddress) &&
            targetAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{targetHost}]:{profile.TargetPort.ToString(CultureInfo.InvariantCulture)}"
            : $"{targetHost}:{profile.TargetPort.ToString(CultureInfo.InvariantCulture)}";

        var arguments = new List<string>
        {
            "-T",
            "-W",
            target,
            "-p",
            sshEndpoint.Port.ToString(CultureInfo.InvariantCulture),
            "-F",
            "NUL",
            "-o",
            "BatchMode=yes",
            "-o",
            "StrictHostKeyChecking=yes",
            "-o",
            $"UserKnownHostsFile={knownHostsPath}",
            "-o",
            "GlobalKnownHostsFile=NUL",
            "-o",
            "UpdateHostKeys=no",
            "-o",
            "CheckHostIP=no",
        };

        if (profile.PrivateKeyPath is not null)
        {
            ValidatePath(profile.PrivateKeyPath, nameof(profile.PrivateKeyPath));
            arguments.Add("-o");
            arguments.Add("IdentitiesOnly=yes");
            arguments.Add("-o");
            arguments.Add("PreferredAuthentications=publickey");
            arguments.Add("-i");
            arguments.Add(profile.PrivateKeyPath);
        }
        else
        {
            arguments.Add("-o");
            arguments.Add("IdentitiesOnly=no");
            arguments.Add("-o");
            arguments.Add("PreferredAuthentications=publickey");
        }

        arguments.Add($"{profile.Username}@{sshEndpoint.Host}");
        return new OpenSshProcessStart(sshPath, arguments);
    }

    public static OpenSshProcessStart BuildKeyScan(
        string keyScanPath,
        SshHostKeyEndpoint endpoint,
        TimeSpan timeout)
    {
        ValidateExecutablePath(
            keyScanPath,
            "ssh-keyscan.exe",
            nameof(keyScanPath));
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Keyscan timeout must be finite and positive.");
        }

        var wholeSeconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        return new OpenSshProcessStart(
            keyScanPath,
            [
                "-T",
                wholeSeconds.ToString(CultureInfo.InvariantCulture),
                "-p",
                endpoint.Port.ToString(CultureInfo.InvariantCulture),
                endpoint.Host,
            ]);
    }

    private static void ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username) ||
            username[0] == '-' ||
            username.Contains('@', StringComparison.Ordinal) ||
            username.Any(char.IsControl))
        {
            throw new ArgumentException("SSH username contains characters that are unsafe for OpenSSH.", nameof(username));
        }
    }

    private static string CanonicalHost(string host, string parameterName)
    {
        if (host.TrimStart().StartsWith('-') || host.Any(char.IsControl))
        {
            throw new ArgumentException("SSH host contains unsafe characters.", parameterName);
        }

        return new SshHostKeyEndpoint(host, 22).Host;
    }

    private static void ValidatePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
        {
            throw new ArgumentException("Path cannot be blank or contain control characters.", parameterName);
        }
    }

    private static void ValidateExecutablePath(
        string path,
        string expectedFileName,
        string parameterName)
    {
        ValidatePath(path, parameterName);
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(
                Path.GetFileName(path),
                expectedFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"OpenSSH executable must be an absolute path named '{expectedFileName}'.",
                parameterName);
        }
    }
}
