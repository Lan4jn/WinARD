namespace WinARD.ProtocolProbe;

public enum ProbeMode
{
    Authentication,
    CaptureFirstFrame,
    PointerSmoke,
}

public sealed record ProbeRequest(ProbeMode Mode, string? CaptureFirstFramePath = null);

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
            request = new ProbeRequest(ProbeMode.CaptureFirstFrame, args[1]);
            return true;
        }

        return false;
    }
}
