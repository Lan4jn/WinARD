namespace WinARD.ProtocolProbe.RdmCapture;

public static class RdmCaptureComparer
{
    private const int CurrentSchemaVersion = 1;

    public static int FindSingleAdaptiveOnlyEncoding(
        RdmCaptureReport baseline,
        RdmCaptureReport adaptive)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(adaptive);

        baseline = Snapshot(baseline);
        adaptive = Snapshot(adaptive);

        if (baseline.SchemaVersion != adaptive.SchemaVersion)
        {
            throw new InvalidDataException(
                $"Capture schemas differ: {baseline.SchemaVersion} and {adaptive.SchemaVersion}.");
        }

        if (baseline.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported capture schema {baseline.SchemaVersion}.");
        }

        if (baseline.Encodings is null || adaptive.Encodings is null)
        {
            throw new InvalidDataException(
                $"Capture encoding list is missing for schema {CurrentSchemaVersion}.");
        }

        var baselineEncodings = baseline.Encodings.ToHashSet();
        var adaptiveOnly = new List<int>();
        var seenAdaptiveOnly = new HashSet<int>();
        foreach (var encoding in adaptive.Encodings)
        {
            if (!baselineEncodings.Contains(encoding) && seenAdaptiveOnly.Add(encoding))
            {
                adaptiveOnly.Add(encoding);
            }
        }

        if (adaptiveOnly.Count != 1)
        {
            throw new InvalidDataException(
                $"Schema {CurrentSchemaVersion} comparison found {adaptiveOnly.Count} distinct adaptive-only encoding IDs.");
        }

        return adaptiveOnly[0];
    }

    private static RdmCaptureReport Snapshot(RdmCaptureReport report) =>
        new(
            report.SchemaVersion,
            report.Profile,
            report.ClientVersion,
            report.ClientInit,
            report.PixelFormat,
            report.Encodings,
            report.Messages,
            report.ReachedFramebufferRequest,
            report.StoppedAtUnknownMessageType);
}
