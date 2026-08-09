using System.Text.Json;

namespace WinARD.ProtocolProbe.RdmCapture;

public static class RdmCaptureFile
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumFileLength = 4 * 1024 * 1024;
    private const int MaximumListCount = 4096;
    private const int MaximumClientVersionLength = 32;
    private const int MaximumMessageNameLength = 64;
    private const int MaximumNumericPayloadHexLength = 4096;
    private const int Sha256HexLength = 64;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task WriteAsync(
        string path,
        RdmCaptureReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(report);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var temporaryPath = Path.Combine(
            directory,
            $".winard-rdm-{Guid.NewGuid():N}.tmp");

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Snapshot(report);
        Validate(snapshot);
        Directory.CreateDirectory(directory);

        Exception? failure = null;
        var temporaryCreated = false;
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                temporaryCreated = true;
                await JsonSerializer.SerializeAsync(
                    stream,
                    snapshot,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                ValidateFileLength(stream.Length);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (temporaryCreated)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception cleanupException) when (failure is not null)
                {
                    failure.Data["RdmCaptureTemporaryFileCleanupException"] = cleanupException;
                }
            }
        }
    }

    public static async Task<RdmCaptureReport> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);

        var fullPath = Path.GetFullPath(path);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            ValidateFileLength(stream.Length);
            var report = await JsonSerializer.DeserializeAsync<RdmCaptureReport>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (report is null)
            {
                throw new InvalidDataException("RDM capture is empty.");
            }

            Validate(report);
            return report;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("RDM capture JSON is invalid.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("RDM capture JSON is invalid.", exception);
        }
        catch (ArgumentNullException exception)
        {
            throw new InvalidDataException("RDM capture JSON is invalid.", exception);
        }
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

    private static void Validate(RdmCaptureReport report)
    {
        if (report.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported RDM capture schema {report.SchemaVersion}.");
        }

        if (!IsValidProfile(report.Profile))
        {
            throw new InvalidDataException("RDM capture profile is invalid.");
        }

        if (report.ClientVersion is null || report.ClientVersion.Length > MaximumClientVersionLength)
        {
            throw new InvalidDataException("RDM capture client version is invalid.");
        }

        if (report.Encodings is null || report.Encodings.Count > MaximumListCount)
        {
            throw new InvalidDataException("RDM capture encoding list is invalid.");
        }

        if (report.Messages is null || report.Messages.Count > MaximumListCount)
        {
            throw new InvalidDataException("RDM capture message list is invalid.");
        }

        foreach (var message in report.Messages)
        {
            if (message is null
                || message.Name is null
                || message.Name.Length > MaximumMessageNameLength)
            {
                throw new InvalidDataException("RDM capture message is invalid.");
            }

            if (!IsValidNumericPayloadHex(message.NumericPayloadHex))
            {
                throw new InvalidDataException("RDM capture numeric payload is invalid.");
            }

            if (!IsValidPayloadSha256(message.PayloadSha256))
            {
                throw new InvalidDataException("RDM capture payload SHA-256 is invalid.");
            }
        }
    }

    private static void ValidateFileLength(long length)
    {
        if (length > MaximumFileLength)
        {
            throw new InvalidDataException(
                $"RDM capture exceeds the maximum file length of {MaximumFileLength} bytes.");
        }
    }

    private static bool IsValidProfile(string? profile)
    {
        if (profile is not { Length: >= 1 and <= 32 })
        {
            return false;
        }

        foreach (var character in profile)
        {
            if (character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidNumericPayloadHex(string? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value.Length > MaximumNumericPayloadHexLength || (value.Length & 1) != 0)
        {
            return false;
        }

        return IsUppercaseHex(value);
    }

    private static bool IsValidPayloadSha256(string? value)
    {
        if (value is null)
        {
            return true;
        }

        return value.Length == Sha256HexLength && IsUppercaseHex(value);
    }

    private static bool IsUppercaseHex(string value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }
}
