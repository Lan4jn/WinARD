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

    public static void EnsureDestinationAvailable(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(fullDirectory) && Directory.EnumerateFileSystemEntries(fullDirectory).Any())
        {
            throw new IOException("The encoding prefix output directory is not empty; existing files will not be overwritten.");
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
            capture.PayloadPrefix);
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
                SyntheticScreenConfirmed: true);
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

    private sealed record CaptureManifest(
        int SchemaVersion,
        int EncodingId,
        CapturedRectangle Rectangle,
        int PrefixLength,
        string PayloadSha256,
        bool SyntheticScreenConfirmed);
}
