using System.Text.Json;

namespace WinARD.ProtocolProbe.EncodingResearch;

public static class EncodingPrefixCaptureFile
{
    private const string ConfirmationError =
        "Encoding payload capture requires an explicit synthetic-screen confirmation.";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    public static IReadOnlyList<string> RequiredSampleNames { get; } =
        ["solid-color", "gradient", "text", "multiple-rectangle-sizes"];
    public static IReadOnlyList<string> RequiredCaptureVariantNames { get; } =
    [
        "solid-color",
        "gradient",
        "text",
        "multiple-rectangle-sizes/small",
        "multiple-rectangle-sizes/medium",
        "multiple-rectangle-sizes/large",
    ];

    public static void EnsureDestinationAvailable(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullDirectory = Path.GetFullPath(outputDirectory);
        RejectReparsePoints(fullDirectory);
        if (Directory.Exists(fullDirectory) || File.Exists(fullDirectory))
        {
            throw new IOException("The encoding prefix output directory already exists; existing paths will not be overwritten.");
        }
    }

    public static async Task WriteAsync(
        string outputDirectory,
        EncodingPrefixCapture capture,
        bool syntheticScreenConfirmed,
        CancellationToken cancellationToken)
    {
        if (!syntheticScreenConfirmed)
        {
            throw new InvalidOperationException(ConfirmationError);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(capture);
        var snapshot = new EncodingPrefixCapture(
            capture.SchemaVersion,
            capture.EncodingId,
            capture.Rectangle,
            capture.PrefixLength,
            capture.PayloadSha256,
            capture.PayloadPrefix,
            capture.DeclaredPayloadLength);
        Validate(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDestinationAvailable(outputDirectory);

        var fullDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullDirectory);
        var manifestPath = Path.Combine(fullDirectory, "manifest.json");
        var payloadPath = Path.Combine(fullDirectory, "payload-prefix.bin");
        var manifestCreated = false;
        var payloadCreated = false;
        try
        {
            await using (var payload = new FileStream(
                payloadPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                payloadCreated = true;
                await payload.WriteAsync(snapshot.PayloadPrefix, cancellationToken).ConfigureAwait(false);
                await payload.FlushAsync(cancellationToken).ConfigureAwait(false);
                payload.Flush(flushToDisk: true);
            }

            var manifest = new CaptureManifest(
                snapshot.SchemaVersion,
                snapshot.EncodingId,
                snapshot.Rectangle,
                snapshot.PrefixLength,
                snapshot.PayloadSha256,
                snapshot.DeclaredPayloadLength,
                snapshot.PrefixLength,
                SyntheticScreenConfirmed: true,
                Sample: null);
            await using var destination = new FileStream(
                manifestPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true);
            manifestCreated = true;
            await JsonSerializer.SerializeAsync(
                destination,
                manifest,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }
        catch
        {
            if (manifestCreated)
            {
                TryDelete(manifestPath);
            }

            if (payloadCreated)
            {
                TryDelete(payloadPath);
            }

            throw;
        }
    }

    public static Task WriteSampleAsync(
        string outputDirectory,
        int candidateEncodingId,
        string sampleName,
        EncodingPrefixCapture capture,
        bool syntheticScreenConfirmed,
        CancellationToken cancellationToken)
    {
        RequireSyntheticScreenConfirmation(syntheticScreenConfirmed);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleName);
        if (candidateEncodingId is not (1002 or 1001))
        {
            throw new ArgumentOutOfRangeException(nameof(candidateEncodingId));
        }

        if (!RequiredCaptureVariantNames.Contains(sampleName, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleName));
        }

        if (capture.EncodingId != candidateEncodingId)
        {
            throw new InvalidDataException("The capture encoding does not match the candidate encoding.");
        }

        var sampleDirectory = Path.Combine(
            Path.GetFullPath(outputDirectory),
            sampleName);
        return WriteSampleCoreAsync(
            sampleDirectory,
            sampleName,
            capture,
            cancellationToken);
    }

    public static async Task WriteSetManifestAsync(
        string stagingDirectory,
        int candidateEncodingId,
        IReadOnlyList<EncodingPrefixCapture> captures,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(captures);
        if (candidateEncodingId is not (1002 or 1001)
            || captures.Count != RequiredCaptureVariantNames.Count
            || captures.Any(capture => capture.EncodingId != candidateEncodingId))
        {
            throw new InvalidDataException("The encoding prefix capture set is incomplete or invalid.");
        }

        var distinctMultipleRectangleSizes = RequiredCaptureVariantNames
            .Select((name, index) => (name, index))
            .Where(item => item.name.StartsWith("multiple-rectangle-sizes/", StringComparison.Ordinal))
            .Select(item => captures[item.index].Rectangle)
            .Select(rectangle => (rectangle.Width, rectangle.Height))
            .Distinct()
            .Count();
        if (distinctMultipleRectangleSizes < 2)
        {
            throw new InvalidDataException(
                "The multiple-rectangle-sizes sample requires at least two distinct wire rectangle sizes.");
        }

        var manifest = new CaptureSetManifest(
            SchemaVersion: 1,
            candidateEncodingId,
            Status: "complete",
            RequiredCaptureVariantNames.Zip(
                captures,
                (name, capture) => new CaptureSetSample(
                    name,
                    capture.Rectangle,
                    capture.PayloadSha256)).ToArray());
        var path = Path.Combine(Path.GetFullPath(stagingDirectory), "capture-set.json");
        await using var destination = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(destination, manifest, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static async Task WriteSampleCoreAsync(
        string sampleDirectory,
        string sampleName,
        EncodingPrefixCapture capture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var snapshot = new EncodingPrefixCapture(
            capture.SchemaVersion,
            capture.EncodingId,
            capture.Rectangle,
            capture.PrefixLength,
            capture.PayloadSha256,
            capture.PayloadPrefix,
            capture.DeclaredPayloadLength);
        Validate(snapshot);
        EnsureDestinationAvailable(sampleDirectory);
        Directory.CreateDirectory(sampleDirectory);
        var payloadPath = Path.Combine(sampleDirectory, "payload-prefix.bin");
        var manifestPath = Path.Combine(sampleDirectory, "manifest.json");
        var payloadCreated = false;
        var manifestCreated = false;
        try
        {
            await using (var payload = new FileStream(
                payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                payloadCreated = true;
                await payload.WriteAsync(snapshot.PayloadPrefix, cancellationToken).ConfigureAwait(false);
                await payload.FlushAsync(cancellationToken).ConfigureAwait(false);
                payload.Flush(flushToDisk: true);
            }
            var manifest = new CaptureManifest(
                snapshot.SchemaVersion,
                snapshot.EncodingId,
                snapshot.Rectangle,
                snapshot.PrefixLength,
                snapshot.PayloadSha256,
                snapshot.DeclaredPayloadLength,
                snapshot.PrefixLength,
                SyntheticScreenConfirmed: true,
                Sample: sampleName);
            await using var destination = new FileStream(
                manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            manifestCreated = true;
            await JsonSerializer.SerializeAsync(destination, manifest, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }
        catch
        {
            if (manifestCreated)
            {
                TryDelete(manifestPath);
            }

            if (payloadCreated)
            {
                TryDelete(payloadPath);
            }

            throw;
        }
    }

    internal static void RequireSyntheticScreenConfirmation(bool syntheticScreenConfirmed)
    {
        if (!syntheticScreenConfirmed)
        {
            throw new InvalidOperationException(ConfirmationError);
        }
    }

    private static void Validate(EncodingPrefixCapture capture)
    {
        if (capture.SchemaVersion != 1
            || capture.PayloadPrefix is null
            || capture.PrefixLength != capture.PayloadPrefix.Length
            || capture.PrefixLength is < 1 or > 64 * 1024
            || capture.DeclaredPayloadLength < capture.PrefixLength
            || capture.PayloadSha256 is not { Length: 64 }
            || !string.Equals(
                capture.PayloadSha256,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(capture.PayloadPrefix)),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The encoding prefix capture is invalid.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RejectReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Encoding prefix output paths must not contain reparse points.");
            }
        }
    }

    private sealed record CaptureManifest(
        int SchemaVersion,
        int EncodingId,
        CapturedRectangle Rectangle,
        int PrefixLength,
        string PayloadSha256,
        uint DeclaredPayloadLength,
        int ObservedPrefixLength,
        bool SyntheticScreenConfirmed,
        string? Sample);

    private sealed record CaptureSetManifest(
        int SchemaVersion,
        int CandidateEncodingId,
        string Status,
        IReadOnlyList<CaptureSetSample> Samples);

    private sealed record CaptureSetSample(
        string Name,
        CapturedRectangle Rectangle,
        string PayloadSha256);
}
