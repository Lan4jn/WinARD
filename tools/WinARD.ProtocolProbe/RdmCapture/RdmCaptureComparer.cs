namespace WinARD.ProtocolProbe.RdmCapture;

public sealed record RdmComparisonResult(
    IReadOnlyList<int> AddedEncodings,
    IReadOnlyList<int> RemovedEncodings,
    IReadOnlyList<int> CommonEncodingsInBaselineOrder,
    IReadOnlyList<int> CommonEncodingsInAdaptiveOrder,
    bool HasEncodingOrderDifference,
    bool PixelFormatDiffers,
    string? PixelFormatSummary,
    bool MessageSequenceDiffers,
    string? MessageSequenceSummary,
    bool StoppedAtUnknownMessageTypeDiffers,
    byte? BaselineStoppedAtUnknownMessageType,
    byte? AdaptiveStoppedAtUnknownMessageType,
    string ObservationSummary);

public static class RdmCaptureComparer
{
    private const int CurrentSchemaVersion = 1;

    public static RdmComparisonResult Compare(
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

        var baselineEncodingsSet = baseline.Encodings.ToHashSet();
        var adaptiveEncodingsSet = adaptive.Encodings.ToHashSet();

        var addedEncodings = new List<int>();
        var seenAdded = new HashSet<int>();
        foreach (var encoding in adaptive.Encodings)
        {
            if (!baselineEncodingsSet.Contains(encoding) && seenAdded.Add(encoding))
            {
                addedEncodings.Add(encoding);
            }
        }

        var removedEncodings = new List<int>();
        var seenRemoved = new HashSet<int>();
        foreach (var encoding in baseline.Encodings)
        {
            if (!adaptiveEncodingsSet.Contains(encoding) && seenRemoved.Add(encoding))
            {
                removedEncodings.Add(encoding);
            }
        }

        var commonInBaseline = new List<int>();
        var seenCommonBaseline = new HashSet<int>();
        foreach (var encoding in baseline.Encodings)
        {
            if (adaptiveEncodingsSet.Contains(encoding) && seenCommonBaseline.Add(encoding))
            {
                commonInBaseline.Add(encoding);
            }
        }

        var commonInAdaptive = new List<int>();
        var seenCommonAdaptive = new HashSet<int>();
        foreach (var encoding in adaptive.Encodings)
        {
            if (baselineEncodingsSet.Contains(encoding) && seenCommonAdaptive.Add(encoding))
            {
                commonInAdaptive.Add(encoding);
            }
        }

        var hasEncodingOrderDifference = !commonInBaseline.SequenceEqual(commonInAdaptive);

        var pixelFormatDiffers = baseline.PixelFormat != adaptive.PixelFormat;
        string? pixelFormatSummary = null;
        if (pixelFormatDiffers)
        {
            pixelFormatSummary = $"Baseline: {FormatPixelFormat(baseline.PixelFormat)}; Adaptive: {FormatPixelFormat(adaptive.PixelFormat)}";
        }

        var messageSequenceDiffers = !AreMessageSequencesEquivalent(baseline.Messages, adaptive.Messages);
        string? messageSequenceSummary = null;
        if (messageSequenceDiffers)
        {
            var baseNames = string.Join(", ", baseline.Messages?.Select(m => m.Name) ?? []);
            var adaptNames = string.Join(", ", adaptive.Messages?.Select(m => m.Name) ?? []);
            messageSequenceSummary = $"Baseline: [{baseNames}]; Adaptive: [{adaptNames}]";
        }

        var stoppedDiffers = baseline.StoppedAtUnknownMessageType != adaptive.StoppedAtUnknownMessageType;

        var observations = new List<string>();
        var declarationsComplete = IsComplete(baseline) && IsComplete(adaptive);
        if (!declarationsComplete)
        {
            observations.Add("Declaration capture is incomplete; observed partial differences cannot establish equivalent capabilities or a unique candidate.");
        }
        if (declarationsComplete && addedEncodings.Count == 0 &&
            removedEncodings.Count == 0 &&
            !hasEncodingOrderDifference &&
            !pixelFormatDiffers &&
            !messageSequenceDiffers &&
            !stoppedDiffers)
        {
            observations.Add("No encoding or declaration differences observed between baseline and adaptive.");
        }
        else
        {
            if (addedEncodings.Count > 0)
            {
                observations.Add($"Observed {addedEncodings.Count} adaptive-only encoding(s): [{string.Join(", ", addedEncodings)}].");
            }
            if (removedEncodings.Count > 0)
            {
                observations.Add($"Observed {removedEncodings.Count} baseline-only encoding(s): [{string.Join(", ", removedEncodings)}].");
            }
            if (hasEncodingOrderDifference)
            {
                observations.Add("Common encoding declaration order differs.");
            }
            if (pixelFormatDiffers)
            {
                observations.Add($"Pixel format differs ({pixelFormatSummary}).");
            }
            if (messageSequenceDiffers)
            {
                observations.Add($"Message sequence differs ({messageSequenceSummary}).");
            }
            if (stoppedDiffers)
            {
                observations.Add($"Stopped at unknown message status differs (Baseline={baseline.StoppedAtUnknownMessageType?.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) ?? "None"}, Adaptive={adaptive.StoppedAtUnknownMessageType?.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) ?? "None"}).");
            }
        }

        observations.Add("Note: Client-side declarations represent candidate capabilities from RDM and do not confirm server-side codec selection or proprietary video codec support.");

        var observationSummary = string.Join(" ", observations);

        return new RdmComparisonResult(
            addedEncodings,
            removedEncodings,
            commonInBaseline,
            commonInAdaptive,
            hasEncodingOrderDifference,
            pixelFormatDiffers,
            pixelFormatSummary,
            messageSequenceDiffers,
            messageSequenceSummary,
            stoppedDiffers,
            baseline.StoppedAtUnknownMessageType,
            adaptive.StoppedAtUnknownMessageType,
            observationSummary);
    }

    public static int FindSingleAdaptiveOnlyEncoding(
        RdmCaptureReport baseline,
        RdmCaptureReport adaptive)
    {
        var comparison = Compare(baseline, adaptive);

        if (!IsComplete(baseline) || !IsComplete(adaptive))
        {
            throw new InvalidDataException("A candidate requires complete declarations through the first framebuffer request without an unknown-message stop.");
        }

        if (comparison.AddedEncodings.Count != 1)
        {
            throw new InvalidDataException(
                $"Schema {CurrentSchemaVersion} comparison found {comparison.AddedEncodings.Count} distinct adaptive-only encoding IDs.");
        }

        return comparison.AddedEncodings[0];
    }

    private static bool IsComplete(RdmCaptureReport report) =>
        report.ReachedFramebufferRequest && report.StoppedAtUnknownMessageType is null;

    private static string FormatPixelFormat(CapturedPixelFormat? format)
    {
        if (format is null)
        {
            return "None";
        }

        return $"{format.BitsPerPixel}-bit (Depth={format.Depth}, TrueColor={format.TrueColor}, RMax={format.RedMax}, GMax={format.GreenMax}, BMax={format.BlueMax})";
    }

    private static bool AreMessageSequencesEquivalent(
        IReadOnlyList<CapturedClientMessage>? first,
        IReadOnlyList<CapturedClientMessage>? second)
    {
        if (first is null && second is null)
        {
            return true;
        }
        if (first is null || second is null)
        {
            return false;
        }
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var i = 0; i < first.Count; i++)
        {
            var a = first[i];
            var b = second[i];
            if (a.Type != b.Type ||
                a.Name != b.Name ||
                a.WireLength != b.WireLength ||
                a.NumericPayloadHex != b.NumericPayloadHex ||
                a.PayloadSha256 != b.PayloadSha256)
            {
                return false;
            }
        }

        return true;
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
