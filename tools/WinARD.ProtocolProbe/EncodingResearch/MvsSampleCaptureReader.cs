using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.ProtocolProbe.EncodingResearch;

public sealed class MvsBoundaryDesynchronizedException(string message) : IOException(message);

public sealed class MvsQuotaExceededException(string message) : IOException(message);

public static class MvsSampleCaptureReader
{
    public const int DefaultMaximumPrefixLength = 64 * 1024;
    public const int MaximumSingleRecordPayloadBytes = 16 * 1024 * 1024; // 16 MiB
    public const int MaximumBatchPayloadBytes = 64 * 1024 * 1024; // 64 MiB
    public const int MaximumBatchRecordCount = 64;
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(25);
    public static readonly TimeSpan DefaultBatchTimeout = TimeSpan.FromSeconds(120);
    private const int CurrentSchemaVersion = 1;
    private const int MaximumRectangleCount = 4096;
    private const int MaximumEmptyUpdates = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<MvsSampleCapture> ReadCompleteRecordAsync(
        Stream stream,
        int candidateEncodingId,
        string sampleName,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout = null,
        ushort? framebufferWidth = null,
        ushort? framebufferHeight = null,
        bool validateSuccessorBoundary = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleName);

        var timeout = operationTimeout ?? DefaultOperationTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);
        var token = linkedCts.Token;

        var reader = new RfbReader(stream, ProtocolLimits.Default);
        byte messageType;
        do
        {
            messageType = await reader.ReadByteAsync(token).ConfigureAwait(false);
        }
        while (ArdServerMessage.IsZeroPayloadControl(messageType));

        if (messageType != 0)
        {
            throw new RfbProtocolException(
                $"Expected FramebufferUpdate message type 0, received {messageType}.");
        }

        _ = await reader.ReadByteAsync(token).ConfigureAwait(false); // padding
        var rectangleCount = await reader.ReadUInt16Async(token).ConfigureAwait(false);
        if (rectangleCount > MaximumRectangleCount)
        {
            throw new RfbProtocolException(
                $"FramebufferUpdate rectangle count {rectangleCount} exceeds the limit of {MaximumRectangleCount}.");
        }

        if (rectangleCount == 0)
        {
            throw new RfbProtocolException("FramebufferUpdate contained 0 rectangles.");
        }

        var x = await reader.ReadUInt16Async(token).ConfigureAwait(false);
        var y = await reader.ReadUInt16Async(token).ConfigureAwait(false);
        var width = await reader.ReadUInt16Async(token).ConfigureAwait(false);
        var height = await reader.ReadUInt16Async(token).ConfigureAwait(false);
        var encodingId = await reader.ReadInt32Async(token).ConfigureAwait(false);

        if (encodingId != candidateEncodingId)
        {
            throw new EncodingCandidateNotObservedException(encodingId);
        }

        var rectangle = new CapturedRectangle(x, y, width, height);
        var isControl = x == 0 && y == 0 && width == 0 && height == 0;
        var isSlice = width > 0 && height > 0;

        if (!isControl && !isSlice)
        {
            throw new RfbProtocolException(
                $"Candidate rectangle ({x},{y}) {width}x{height} is neither a recognized origin control record nor a non-zero image slice.");
        }

        if (isSlice)
        {
            if (framebufferWidth is { } fw && checked((uint)x + width) > fw)
            {
                throw new RfbProtocolException($"Rectangle width {width} at X={x} exceeds framebuffer width {fw}.");
            }
            if (framebufferHeight is { } fh && checked((uint)y + height) > fh)
            {
                throw new RfbProtocolException($"Rectangle height {height} at Y={y} exceeds framebuffer height {fh}.");
            }
        }

        var lengthHeader = await reader.ReadBytesAsync(4, token).ConfigureAwait(false);
        var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(lengthHeader);
        if (declaredLength > MaximumSingleRecordPayloadBytes)
        {
            throw new MvsQuotaExceededException(
                $"Declared record payload length {declaredLength} exceeds the single-record limit of {MaximumSingleRecordPayloadBytes} bytes.");
        }

        var payload = await reader.ReadBytesAsync(checked((int)declaredLength), token).ConfigureAwait(false);

        // Compute SHA-256 over entire record payload (including length header)
        var fullRecordData = new byte[4 + payload.Length];
        Buffer.BlockCopy(lengthHeader, 0, fullRecordData, 0, 4);
        Buffer.BlockCopy(payload, 0, fullRecordData, 4, payload.Length);
        var payloadSha256 = Convert.ToHexString(SHA256.HashData(fullRecordData));

        var prefixLength = Math.Min(fullRecordData.Length, DefaultMaximumPrefixLength);
        var payloadPrefix = new byte[prefixLength];
        Buffer.BlockCopy(fullRecordData, 0, payloadPrefix, 0, prefixLength);

        var completeness = MvsRecordCompleteness.RecordCompleteUnderLengthHypothesis;
        byte? successorMessageType = null;
        int? successorEncodingId = null;

        if (validateSuccessorBoundary)
        {
            if (rectangleCount > 1)
            {
                // Validate next rectangle header in the same FramebufferUpdate
                _ = await reader.ReadUInt16Async(token).ConfigureAwait(false); // nextX
                _ = await reader.ReadUInt16Async(token).ConfigureAwait(false); // nextY
                _ = await reader.ReadUInt16Async(token).ConfigureAwait(false); // nextW
                _ = await reader.ReadUInt16Async(token).ConfigureAwait(false); // nextH
                var nextEncId = await reader.ReadInt32Async(token).ConfigureAwait(false);
                if (nextEncId is not (1011 or 6 or 0 or 16 or -239 or -223 or 1100 or 1101 or 1105 or 1 or 2))
                {
                    throw new MvsBoundaryDesynchronizedException(
                        $"Successor rectangle boundary desynchronized; unexpected encoding ID {nextEncId}.");
                }
                successorEncodingId = nextEncId;
                completeness = MvsRecordCompleteness.SuccessorBoundaryValidated;
            }
            else
            {
                // Validate next server message type
                var nextMsgType = await reader.ReadByteAsync(token).ConfigureAwait(false);
                if (nextMsgType is not (0 or 2 or 3))
                {
                    throw new MvsBoundaryDesynchronizedException(
                        $"Successor server message boundary desynchronized; unexpected message type {nextMsgType}.");
                }
                successorMessageType = nextMsgType;
                completeness = MvsRecordCompleteness.SuccessorBoundaryValidated;
            }
        }

        var signature = DetectHeuristicSignature(payloadPrefix);

        return new MvsSampleCapture(
            SchemaVersion: CurrentSchemaVersion,
            EncodingId: encodingId,
            SampleName: sampleName,
            Rectangle: rectangle,
            PrefixLength: prefixLength,
            PayloadSha256: payloadSha256,
            PayloadPrefix: fullRecordData, // Contains full record under hypothesis
            HeuristicBigEndianLength: declaredLength,
            HeuristicSignature: signature,
            TimestampUtc: DateTimeOffset.UtcNow,
            Completeness: completeness,
            RecordKind: isControl ? MvsRecordKind.ControlSetup : MvsRecordKind.ImageSlice,
            DeclaredLength: declaredLength,
            ActualLength: payload.Length,
            SuccessorMessageType: successorMessageType,
            SuccessorEncodingId: successorEncodingId);
    }

    public static async Task<MvsSampleCapture> ReadAsync(
        Stream stream,
        int candidateEncodingId,
        string sampleName,
        CancellationToken cancellationToken,
        int maximumPrefixLength = DefaultMaximumPrefixLength,
        TimeSpan? operationTimeout = null,
        ushort? framebufferWidth = null,
        ushort? framebufferHeight = null,
        Func<CancellationToken, Task>? requestNextUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleName);
        if (maximumPrefixLength is < 1 or > DefaultMaximumPrefixLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPrefixLength),
                $"Maximum prefix length must be between 1 and {DefaultMaximumPrefixLength} bytes.");
        }

        var timeout = operationTimeout ?? DefaultOperationTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);
        var token = linkedCts.Token;

        var reader = new RfbReader(stream, ProtocolLimits.Default);
        for (var updateIndex = 0; updateIndex < MaximumEmptyUpdates; updateIndex++)
        {
            byte messageType;
            do
            {
                messageType = await reader.ReadByteAsync(token).ConfigureAwait(false);
            }
            while (ArdServerMessage.IsZeroPayloadControl(messageType));

            if (messageType != 0)
            {
                throw new RfbProtocolException(
                    $"Expected FramebufferUpdate message type 0, received {messageType}.");
            }

            _ = await reader.ReadByteAsync(token).ConfigureAwait(false);
            var rectangleCount = await reader.ReadUInt16Async(token).ConfigureAwait(false);
            if (rectangleCount > MaximumRectangleCount)
            {
                throw new RfbProtocolException(
                    $"FramebufferUpdate rectangle count {rectangleCount} exceeds the limit of {MaximumRectangleCount}.");
            }

            if (rectangleCount == 0)
            {
                if (updateIndex + 1 < MaximumEmptyUpdates && requestNextUpdate is not null)
                {
                    await requestNextUpdate(token).ConfigureAwait(false);
                }

                continue;
            }

            var x = await reader.ReadUInt16Async(token).ConfigureAwait(false);
            var y = await reader.ReadUInt16Async(token).ConfigureAwait(false);
            var width = await reader.ReadUInt16Async(token).ConfigureAwait(false);
            var height = await reader.ReadUInt16Async(token).ConfigureAwait(false);
            var encodingId = await reader.ReadInt32Async(token).ConfigureAwait(false);

            if (encodingId != candidateEncodingId)
            {
                throw new EncodingCandidateNotObservedException(encodingId);
            }

            var rectangle = new CapturedRectangle(x, y, width, height);
            var isControl = x == 0 && y == 0 && width == 0 && height == 0;
            var isSlice = width > 0 && height > 0;

            if (!isControl && !isSlice)
            {
                throw new RfbProtocolException(
                    "The candidate rectangle must have non-zero dimensions within the framebuffer unless it is a recognized control record.");
            }

            if (isSlice)
            {
                if (framebufferWidth is { } fw && checked((uint)x + width) > fw)
                {
                    throw new RfbProtocolException("Rectangle exceeds framebuffer width.");
                }
                if (framebufferHeight is { } fh && checked((uint)y + height) > fh)
                {
                    throw new RfbProtocolException("Rectangle exceeds framebuffer height.");
                }
            }

            var prefixBuffer = new byte[maximumPrefixLength];
            var firstRead = await stream.ReadAsync(prefixBuffer.AsMemory(0, 1), token).ConfigureAwait(false);
            if (firstRead == 0)
            {
                throw new EndOfStreamException("The candidate encoding rectangle contained no observable payload data.");
            }

            var totalBytesRead = 1;
            while (totalBytesRead < maximumPrefixLength)
            {
                var availableRead = await stream.ReadAsync(
                    prefixBuffer.AsMemory(totalBytesRead, maximumPrefixLength - totalBytesRead),
                    token).ConfigureAwait(false);
                if (availableRead == 0)
                {
                    break;
                }

                totalBytesRead += availableRead;
                if (stream is MemoryStream ms && ms.Position == ms.Length)
                {
                    break;
                }
            }

            var payloadPrefix = new byte[totalBytesRead];
            Buffer.BlockCopy(prefixBuffer, 0, payloadPrefix, 0, totalBytesRead);

            uint? heuristicLength = null;
            if (totalBytesRead >= 4)
            {
                var candidateLength = BinaryPrimitives.ReadUInt32BigEndian(payloadPrefix.AsSpan(0, 4));
                if (candidateLength is > 0 and <= 100 * 1024 * 1024)
                {
                    heuristicLength = candidateLength;
                }
            }

            var signature = DetectHeuristicSignature(payloadPrefix);

            return new MvsSampleCapture(
                SchemaVersion: CurrentSchemaVersion,
                EncodingId: encodingId,
                SampleName: sampleName,
                Rectangle: rectangle,
                PrefixLength: totalBytesRead,
                PayloadSha256: Convert.ToHexString(SHA256.HashData(payloadPrefix)),
                PayloadPrefix: payloadPrefix,
                HeuristicBigEndianLength: heuristicLength,
                HeuristicSignature: signature,
                TimestampUtc: DateTimeOffset.UtcNow,
                Completeness: MvsRecordCompleteness.PrefixOnly,
                RecordKind: isControl ? MvsRecordKind.ControlSetup : MvsRecordKind.ImageSlice,
                DeclaredLength: heuristicLength,
                ActualLength: totalBytesRead);
        }

        throw new RfbProtocolException(
            $"The server returned {MaximumEmptyUpdates} empty framebuffer updates without a candidate rectangle.");
    }

    public static async Task WriteSampleAsync(
        string destinationDirectory,
        MvsSampleCapture capture,
        CancellationToken cancellationToken,
        string? allowedRootDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(capture);

        var fullDestination = Path.GetFullPath(destinationDirectory);

        if (allowedRootDirectory is not null)
        {
            var fullRoot = Path.GetFullPath(allowedRootDirectory);
            if (!fullDestination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Destination path '{fullDestination}' escapes the allowed root directory '{fullRoot}'.");
            }
        }

        if (Directory.Exists(fullDestination) && Directory.EnumerateFileSystemEntries(fullDestination).Any())
        {
            throw new IOException($"Destination directory '{fullDestination}' already exists and is not empty.");
        }

        var parentDirectory = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidOperationException("Invalid destination parent directory.");
        Directory.CreateDirectory(parentDirectory);

        var stagingDir = Path.Combine(parentDirectory, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        try
        {
            var manifestPath = Path.Combine(stagingDir, "manifest.json");
            var payloadPath = Path.Combine(stagingDir, "payload-prefix.bin");

            await using (var payloadStream = new FileStream(
                payloadPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await payloadStream.WriteAsync(capture.PayloadPrefix, cancellationToken).ConfigureAwait(false);
                await payloadStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Verify written payload hash before publishing
            var writtenBytes = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
            var computedSha = Convert.ToHexString(SHA256.HashData(writtenBytes));
            if (!string.Equals(computedSha, capture.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Payload hash mismatch in staging directory! Expected={capture.PayloadSha256}, Actual={computedSha}");
            }

            await using (var manifestStream = new FileStream(
                manifestPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(manifestStream, capture, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await manifestStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Directory.Exists(fullDestination))
            {
                Directory.Delete(fullDestination, recursive: true);
            }
            Directory.Move(stagingDir, fullDestination);
        }
        catch
        {
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
            }
            throw;
        }
    }

    public static string DetectHeuristicSignature(byte[] prefix)
    {
        if (prefix.Length >= 4 && prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0x00 && prefix[3] == 0x01)
        {
            return "H.264 / H.265 NALU (4-byte start code)";
        }

        if (prefix.Length >= 3 && prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0x01)
        {
            return "H.264 / H.265 NALU (3-byte start code)";
        }

        if (prefix.Length >= 2 && prefix[0] == 0x78 && ((prefix[0] * 256 + prefix[1]) % 31 == 0))
        {
            return "Zlib Stream";
        }

        if (prefix.Length >= 4)
        {
            var magic = System.Text.Encoding.ASCII.GetString(prefix, 0, 4);
            if (magic is "mvs1" or "MVS1" or "avc1" or "hvc1")
            {
                return $"FourCC Magic ({magic})";
            }
        }

        return "Unknown / Raw Payload Header";
    }
}

