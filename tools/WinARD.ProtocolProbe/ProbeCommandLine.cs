namespace WinARD.ProtocolProbe;

public enum ProbeMode
{
    Authentication,
    CaptureFirstFrame,
    PointerSmoke,
    ListenRdm,
    CompareRdmCaptures,
    CaptureKnownEncodingPrefix,
}

public sealed record ProbeRequest(
    ProbeMode Mode,
    string? OutputPath = null,
    string? ProfileName = null,
    string? BaselineCapturePath = null,
    string? AdaptiveCapturePath = null,
    bool SyntheticScreenConfirmed = false,
    int? CandidateEncodingId = null)
{
    public string? CaptureFirstFramePath =>
        Mode == ProbeMode.CaptureFirstFrame ? OutputPath : null;
}

public static class ProbeCommandLine
{
    public static bool TryParse(string[] args, out ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(args);

        request = new ProbeRequest(ProbeMode.Authentication);
        if (args.Length == 0)
        {
            return true;
        }

        if (args.Length == 1 && string.Equals(args[0], "--pointer-smoke", StringComparison.Ordinal))
        {
            request = new ProbeRequest(ProbeMode.PointerSmoke);
            return true;
        }

        if (args.Length == 2
            && string.Equals(args[0], "--capture-first-frame", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(args[1]))
        {
            request = new ProbeRequest(ProbeMode.CaptureFirstFrame, OutputPath: args[1]);
            return true;
        }

        if (args.Length == 3
            && string.Equals(args[0], "--listen-rdm", StringComparison.Ordinal)
            && IsValidProfileName(args[1])
            && !string.IsNullOrWhiteSpace(args[2]))
        {
            request = new ProbeRequest(
                ProbeMode.ListenRdm,
                OutputPath: args[2],
                ProfileName: args[1]);
            return true;
        }

        if (args.Length == 3
            && string.Equals(args[0], "--compare-rdm-captures", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(args[1])
            && !string.IsNullOrWhiteSpace(args[2]))
        {
            request = new ProbeRequest(
                ProbeMode.CompareRdmCaptures,
                BaselineCapturePath: args[1],
                AdaptiveCapturePath: args[2]);
            return true;
        }

        if (args.Length == 4
            && string.Equals(args[0], "--capture-known-encoding-prefix", StringComparison.Ordinal)
            && TryParseKnownEncodingId(args[1], out var candidateEncodingId)
            && !string.IsNullOrWhiteSpace(args[2])
            && string.Equals(args[3], "--confirm-synthetic-screen", StringComparison.Ordinal))
        {
            request = new ProbeRequest(
                ProbeMode.CaptureKnownEncodingPrefix,
                OutputPath: args[2],
                SyntheticScreenConfirmed: true,
                CandidateEncodingId: candidateEncodingId);
            return true;
        }

        return false;
    }

    private static bool TryParseKnownEncodingId(string value, out int encodingId)
    {
        if (string.Equals(value, "1002", StringComparison.Ordinal))
        {
            encodingId = 1002;
            return true;
        }

        if (string.Equals(value, "1001", StringComparison.Ordinal))
        {
            encodingId = 1001;
            return true;
        }

        encodingId = 0;
        return false;
    }

    private static bool IsValidProfileName(string value)
    {
        if (value.Length is < 1 or > 32)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9')
                && character != '-')
            {
                return false;
            }
        }

        return true;
    }
}
