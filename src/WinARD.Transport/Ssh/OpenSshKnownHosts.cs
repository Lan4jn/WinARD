using WinARD.Domain.Connections;

namespace WinARD.Transport.Ssh;

internal static class OpenSshKnownHosts
{
    public static string Format(SshHostKeyPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var host = pin.Endpoint.Port == 22
            ? pin.Endpoint.Host
            : $"[{pin.Endpoint.Host}]:{pin.Endpoint.Port}";
        return $"{host} {pin.Algorithm} {pin.PublicKeyBase64}{Environment.NewLine}";
    }
}

internal static class OpenSshKeyScanParser
{
    public static IReadOnlyList<SshHostKeyCandidate> Parse(
        string output,
        SshHostKeyEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(output);
        var candidates = new List<SshHostKeyCandidate>();
        var sawDifferentEndpoint = false;

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3)
            {
                continue;
            }

            if (!MatchesEndpoint(fields[0], endpoint))
            {
                sawDifferentEndpoint = true;
                continue;
            }

            try
            {
                candidates.Add(
                    SshHostKeyVerifier.CreateCandidate(endpoint, fields[1], fields[2]));
            }
            catch (ArgumentException exception)
            {
                throw new OpenSshKeyScanException("ssh-keyscan returned an invalid public key.", exception);
            }
        }

        if (candidates.Count == 0)
        {
            throw new OpenSshKeyScanException(
                sawDifferentEndpoint
                    ? "ssh-keyscan returned a key for a different endpoint."
                    : "ssh-keyscan returned no usable host keys.");
        }

        return candidates;
    }

    private static bool MatchesEndpoint(string hostField, SshHostKeyEndpoint endpoint)
    {
        var expectedWithPort = $"[{endpoint.Host}]:{endpoint.Port}";
        foreach (var value in hostField.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(value, expectedWithPort, StringComparison.OrdinalIgnoreCase) ||
                endpoint.Port == 22 &&
                string.Equals(value, endpoint.Host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class OpenSshKeyScanException : Exception
{
    public OpenSshKeyScanException(string message)
        : base(message)
    {
    }

    public OpenSshKeyScanException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
