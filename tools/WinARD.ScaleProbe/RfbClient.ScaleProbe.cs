using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.ProtocolProbe.EncodingResearch;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Encodings.Mvs;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.IO;
using Framebuffer = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

namespace WinARD.Desktop.Services;

internal enum MvsCaptureMode
{
    Both = 0,
    SetupOnly = 1,
    SliceOnly = 2,
}

internal sealed record MvsCompleteCaptureResult(
    MvsSampleCapture? SetupCapture,
    MvsSampleCapture? SliceCapture,
    bool SuccessorBoundaryValidated,
    byte? NextMessageType);

// Compiled only into the research console; the product's online scale gate is unchanged.
internal sealed partial class RfbClient
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    internal async Task ProbeMvsAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            ThrowIfProtocolUnavailableNoLock();
            if (!_bootstrapState.IsFirstPixelConfirmed || !_transport.IsEncrypted ||
                _receiveActive || _framebufferRequestOutstanding || _qualityTransitionActive)
                throw new InvalidOperationException("MVS probe requires an idle encrypted session.");
            _qualityTransitionActive = true;
        }
        try
        {
            await _messageScheduler.EnqueueBackgroundAsync(
                token => WinARD.Remote.Protocol.Initialization.RfbSessionInitializer.WriteSetEncodingsAsync(
                    _transport, [1011, 6, 0, -239, -223], token),
                cancellationToken).ConfigureAwait(false);
        }
        catch { BeginShutdown(); throw; }
        finally { lock (_lifecycleSync) _qualityTransitionActive = false; }
    }

    internal async Task<MvsSampleCapture> CaptureMvsSampleAsync(
        string sampleName,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        lock (_lifecycleSync)
        {
            ThrowIfProtocolUnavailableNoLock();
            if (!_bootstrapState.IsFirstPixelConfirmed || !_transport.IsEncrypted ||
                _receiveActive || _framebufferRequestOutstanding || _qualityTransitionActive)
            {
                throw new InvalidOperationException("MVS sample capture requires an idle encrypted session.");
            }

            _qualityTransitionActive = true;
        }

        try
        {
            // 1. Send SetEncodings with 1011 prioritized
            await _messageScheduler.EnqueueBackgroundAsync(
                token => WinARD.Remote.Protocol.Initialization.RfbSessionInitializer.WriteSetEncodingsAsync(
                    _transport, [1011, 6, 0, -239, -223], token),
                cancellationToken).ConfigureAwait(false);

            // 2. Attach capture decoder into existing framebuffer update session (preserving persistent zlib inflater state)
            var captureDecoder = new MvsCaptureDecoder(_transport, sampleName, outputDirectory, 64 * 1024);
            var framebufferUpdates = _framebufferUpdates ?? throw new InvalidOperationException("RFB initialization has not completed.");
            framebufferUpdates.RegisterResearchDecoder(captureDecoder);

            // 3. Release qualityTransitionActive lock and drain transition frames until 1011 is received
            lock (_lifecycleSync)
            {
                _qualityTransitionActive = false;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));

            for (var i = 0; i < 64; i++)
            {
                await RequestFramebufferUpdateAsync(incremental: false, deadline.Token).ConfigureAwait(false);
                var message = await ReceiveAsync(deadline.Token).ConfigureAwait(false);
                (message as IDisposable)?.Dispose();
            }

            throw new TimeoutException("MVS encoding 1011 image slice was not observed within 64 update requests.");
        }
        catch (MvsSampleCapturedException captured)
        {
            await MvsSampleCaptureReader.WriteSampleAsync(outputDirectory, captured.Capture, cancellationToken).ConfigureAwait(false);
            return captured.Capture;
        }
        finally
        {
            lock (_lifecycleSync)
            {
                _qualityTransitionActive = false;
            }

            BeginShutdown();
        }
    }

    internal async Task<MvsCompleteCaptureResult> CaptureCompleteMvsAsync(
        string sampleName,
        string outputDirectory,
        MvsCaptureMode mode,
        CancellationToken cancellationToken,
        string? allowedRootDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var setupTargetDir = mode == MvsCaptureMode.SetupOnly ? outputDirectory : Path.Combine(outputDirectory, "setup");
        var sliceTargetDir = mode == MvsCaptureMode.SliceOnly ? outputDirectory : Path.Combine(outputDirectory, "slice");

        if (mode is MvsCaptureMode.Both or MvsCaptureMode.SetupOnly &&
            Directory.Exists(setupTargetDir) && Directory.EnumerateFileSystemEntries(setupTargetDir).Any())
        {
            throw new IOException($"Target setup directory '{setupTargetDir}' already exists and is not empty.");
        }

        if (mode is MvsCaptureMode.Both or MvsCaptureMode.SliceOnly &&
            Directory.Exists(sliceTargetDir) && Directory.EnumerateFileSystemEntries(sliceTargetDir).Any())
        {
            throw new IOException($"Target slice directory '{sliceTargetDir}' already exists and is not empty.");
        }

        lock (_lifecycleSync)
        {
            ThrowIfProtocolUnavailableNoLock();
            if (!_bootstrapState.IsFirstPixelConfirmed || !_transport.IsEncrypted ||
                _receiveActive || _framebufferRequestOutstanding || _qualityTransitionActive)
            {
                throw new InvalidOperationException(
                    $"MVS complete sample capture requires an idle encrypted session. " +
                    $"FirstPixelConfirmed={_bootstrapState.IsFirstPixelConfirmed} Encrypted={_transport.IsEncrypted} " +
                    $"ReceiveActive={_receiveActive} FbOutstanding={_framebufferRequestOutstanding} QualityTransitionActive={_qualityTransitionActive}");
            }

            _qualityTransitionActive = true;
        }

        try
        {
            // 1. Send SetEncodings with 1011 prioritized
            await _messageScheduler.EnqueueBackgroundAsync(
                token => WinARD.Remote.Protocol.Initialization.RfbSessionInitializer.WriteSetEncodingsAsync(
                    _transport, [1011, 6, 0, -239, -223], token),
                cancellationToken).ConfigureAwait(false);

            // 2. Attach complete capture decoder using non-reflective research entry
            var captureDecoder = new MvsCompleteCaptureDecoder(sampleName, outputDirectory, mode, allowedRootDirectory);
            var framebufferUpdates = _framebufferUpdates ?? throw new InvalidOperationException("RFB initialization has not completed.");
            framebufferUpdates.RegisterResearchDecoder(captureDecoder);

            // 3. Release qualityTransitionActive lock and drain frames
            lock (_lifecycleSync)
            {
                _qualityTransitionActive = false;
            }

            using var batchDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            batchDeadline.CancelAfter(MvsSampleCaptureReader.DefaultBatchTimeout);

            for (var i = 0; i < MvsSampleCaptureReader.MaximumBatchRecordCount; i++)
            {
                using var singleDeadline = CancellationTokenSource.CreateLinkedTokenSource(batchDeadline.Token);
                singleDeadline.CancelAfter(MvsSampleCaptureReader.DefaultOperationTimeout);

                await RequestFramebufferUpdateAsync(incremental: false, singleDeadline.Token).ConfigureAwait(false);
                var message = await ReceiveAsync(singleDeadline.Token).ConfigureAwait(false);
                (message as IDisposable)?.Dispose();

                if (captureDecoder.IsCompleted)
                {
                    // Validate successor boundary: request an incremental update and verify next server message
                    var successorValidated = false;
                    byte? nextMsgType = null;
                    using var successorDeadline = CancellationTokenSource.CreateLinkedTokenSource(batchDeadline.Token);
                    successorDeadline.CancelAfter(MvsSampleCaptureReader.DefaultOperationTimeout);
                    try
                    {
                        await RequestFramebufferUpdateAsync(incremental: true, successorDeadline.Token).ConfigureAwait(false);
                        var nextMsg = await ReceiveAsync(successorDeadline.Token).ConfigureAwait(false);
                        try
                        {
                            nextMsgType = nextMsg switch
                            {
                                RemoteFramebufferMessage => 0,
                                RemoteCursorMessage => 0,
                                RemoteBellMessage => 2,
                                RemoteClipboardMessage => 3,
                                _ => null,
                            };
                            successorValidated = nextMsgType == 0;
                        }
                        finally
                        {
                            (nextMsg as IDisposable)?.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        var errorCode = ex switch
                        {
                            OperationCanceledException when successorDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested => "Timeout",
                            OperationCanceledException => "Cancelled",
                            RfbProtocolException protocolEx => protocolEx.Failure is not null ? $"ProtocolError:{protocolEx.Failure.Kind}" : "ProtocolError",
                            EndOfStreamException => "EndOfStream",
                            IOException => "IOException",
                            _ => ex.GetType().Name,
                        };
                        Console.WriteLine($"SuccessorBoundaryValidationFailed={errorCode}");
                        throw new MvsBoundaryDesynchronizedException(
                            $"Successor boundary desynchronized after complete MVS capture: {errorCode}");
                    }

                    if (captureDecoder.SliceCapture is { } slice && successorValidated)
                    {
                        var upgradedCapture = slice with
                        {
                            Completeness = MvsRecordCompleteness.SuccessorBoundaryValidated,
                            SuccessorMessageType = nextMsgType,
                        };
                        var manifestPath = Path.Combine(sliceTargetDir, "manifest.json");
                        await File.WriteAllTextAsync(
                            manifestPath,
                            JsonSerializer.Serialize(upgradedCapture, IndentedJson),
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (mode == MvsCaptureMode.Both)
                    {
                        var summary = new
                        {
                            sampleName,
                            mode = mode.ToString(),
                            hasSetup = captureDecoder.SetupCapture is not null,
                            hasSlice = captureDecoder.SliceCapture is not null,
                            setupDirectory = "setup",
                            sliceDirectory = "slice",
                            successorBoundaryValidated = successorValidated,
                            nextMessageType = nextMsgType,
                            timestampUtc = DateTimeOffset.UtcNow,
                        };
                        var summaryPath = Path.Combine(outputDirectory, "batch-summary.json");
                        await File.WriteAllTextAsync(
                            summaryPath,
                            JsonSerializer.Serialize(summary, IndentedJson),
                            cancellationToken).ConfigureAwait(false);
                    }

                    return new MvsCompleteCaptureResult(
                        captureDecoder.SetupCapture,
                        captureDecoder.SliceCapture,
                        successorValidated,
                        nextMsgType);
                }
            }

            throw new TimeoutException($"MVS capture mode {mode} was not satisfied within {MvsSampleCaptureReader.MaximumBatchRecordCount} update requests.");
        }
        finally
        {
            lock (_lifecycleSync)
            {
                _qualityTransitionActive = false;
            }

            BeginShutdown();
        }
    }

    private sealed class MvsCompleteCaptureDecoder : IRfbEncodingDecoder
    {
        public int EncodingId => 1011;

        private readonly string _sampleName;
        private readonly string _outputDirectory;
        private readonly MvsCaptureMode _mode;
        private readonly string? _allowedRootDirectory;
        private long _totalBatchPayloadBytes;

        public MvsSampleCapture? SetupCapture { get; private set; }
        public MvsSampleCapture? SliceCapture { get; private set; }
        public bool IsCompleted { get; private set; }

        public MvsCompleteCaptureDecoder(
            string sampleName,
            string outputDirectory,
            MvsCaptureMode mode,
            string? allowedRootDirectory)
        {
            _sampleName = sampleName;
            _outputDirectory = outputDirectory;
            _mode = mode;
            _allowedRootDirectory = allowedRootDirectory;
        }

        public async ValueTask<EncodingDecodeResult> DecodeAsync(
            RfbReader reader,
            Framebuffer framebuffer,
            FramebufferRect rectangle,
            CancellationToken cancellationToken)
        {
            var capturedRect = new CapturedRectangle(
                checked((ushort)rectangle.X),
                checked((ushort)rectangle.Y),
                checked((ushort)rectangle.Width),
                checked((ushort)rectangle.Height));

            var isControl = capturedRect.X == 0 && capturedRect.Y == 0 && capturedRect.Width == 0 && capturedRect.Height == 0;
            var isSlice = capturedRect.Width > 0 && capturedRect.Height > 0;

            if (!isControl && !isSlice)
            {
                throw new RfbProtocolException(
                    $"Candidate rectangle ({capturedRect.X},{capturedRect.Y}) {capturedRect.Width}x{capturedRect.Height} is neither recognized origin control nor valid image slice.");
            }

            var lengthHeader = await reader.ReadBytesAsync(4, cancellationToken).ConfigureAwait(false);
            var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(lengthHeader);

            if (declaredLength > MvsSampleCaptureReader.MaximumSingleRecordPayloadBytes)
            {
                throw new MvsQuotaExceededException(
                    $"Declared record payload length {declaredLength} exceeds the single-record limit of {MvsSampleCaptureReader.MaximumSingleRecordPayloadBytes} bytes.");
            }

            _totalBatchPayloadBytes = checked(_totalBatchPayloadBytes + declaredLength + 4);
            if (_totalBatchPayloadBytes > MvsSampleCaptureReader.MaximumBatchPayloadBytes)
            {
                throw new MvsQuotaExceededException(
                    $"Cumulative batch payload bytes {_totalBatchPayloadBytes} exceeds the batch limit of {MvsSampleCaptureReader.MaximumBatchPayloadBytes} bytes.");
            }

            var payload = await reader.ReadBytesAsync(checked((int)declaredLength), cancellationToken).ConfigureAwait(false);

            var fullRecordData = new byte[4 + payload.Length];
            Buffer.BlockCopy(lengthHeader, 0, fullRecordData, 0, 4);
            Buffer.BlockCopy(payload, 0, fullRecordData, 4, payload.Length);
            var sha256 = Convert.ToHexString(SHA256.HashData(fullRecordData));

            var prefixLength = Math.Min(fullRecordData.Length, MvsSampleCaptureReader.DefaultMaximumPrefixLength);
            var payloadPrefix = new byte[prefixLength];
            Buffer.BlockCopy(fullRecordData, 0, payloadPrefix, 0, prefixLength);
            var signature = MvsSampleCaptureReader.DetectHeuristicSignature(payloadPrefix);

            var capture = new MvsSampleCapture(
                SchemaVersion: 1,
                EncodingId: 1011,
                SampleName: _sampleName,
                Rectangle: capturedRect,
                PrefixLength: prefixLength,
                PayloadSha256: sha256,
                PayloadPrefix: fullRecordData,
                HeuristicBigEndianLength: declaredLength,
                HeuristicSignature: signature,
                TimestampUtc: DateTimeOffset.UtcNow,
                Completeness: MvsRecordCompleteness.RecordCompleteUnderLengthHypothesis,
                RecordKind: isControl ? MvsRecordKind.ControlSetup : MvsRecordKind.ImageSlice,
                DeclaredLength: declaredLength,
                ActualLength: payload.Length);

            if (isControl)
            {
                if (_mode is MvsCaptureMode.Both or MvsCaptureMode.SetupOnly)
                {
                    var setupDir = _mode == MvsCaptureMode.SetupOnly
                        ? _outputDirectory
                        : Path.Combine(_outputDirectory, "setup");
                    await MvsSampleCaptureReader.WriteSampleAsync(setupDir, capture, cancellationToken, _allowedRootDirectory).ConfigureAwait(false);
                    SetupCapture = capture;
                    Console.WriteLine($"MvsSetupCaptured=True DeclaredLength={declaredLength} ActualLength={payload.Length} SHA256={sha256}");
                }

                if (_mode == MvsCaptureMode.SetupOnly)
                {
                    IsCompleted = true;
                }

                return EncodingDecodeResult.Empty;
            }

            // isSlice
            if (_mode is MvsCaptureMode.Both or MvsCaptureMode.SliceOnly)
            {
                var sliceDir = _mode == MvsCaptureMode.SliceOnly
                    ? _outputDirectory
                    : Path.Combine(_outputDirectory, "slice");
                await MvsSampleCaptureReader.WriteSampleAsync(sliceDir, capture, cancellationToken, _allowedRootDirectory).ConfigureAwait(false);
                SliceCapture = capture;
                Console.WriteLine($"MvsSliceCaptured=True Rectangle={capturedRect.Width}x{capturedRect.Height} DeclaredLength={declaredLength} ActualLength={payload.Length} SHA256={sha256}");

                if (SetupCapture is not null && payload.Length >= 6 && SetupCapture.PayloadPrefix.Length >= 133)
                {
                    try
                    {
                        byte[] luma = SetupCapture.PayloadPrefix[5..69];
                        byte[] chroma = SetupCapture.PayloadPrefix[69..133];
                        byte[] bgra = new byte[1024];
                        MvsMacroblockParser.DecodeMacroblock(payload, luma, chroma, bgra);

                        byte[] bmp = new byte[54 + 1024];
                        bmp[0] = 0x42;
                        bmp[1] = 0x4D;
                        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2, 4), 54 + 1024);
                        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10, 4), 54);
                        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14, 4), 40);
                        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), 16);
                        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), -16);
                        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26, 2), 1);
                        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28, 2), 32);
                        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(34, 4), 1024);
                        Buffer.BlockCopy(bgra, 0, bmp, 54, 1024);

                        string bmpPath = Path.Combine(sliceDir, "rendered-preview.bmp");
                        await File.WriteAllBytesAsync(bmpPath, bmp, cancellationToken).ConfigureAwait(false);
                        Console.WriteLine($"MvsSliceRendered=True Path={bmpPath} Pixel0=(R={bgra[2]},G={bgra[1]},B={bgra[0]})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"MvsSliceRenderFailed=True Message={ex.Message}");
                    }
                }

                IsCompleted = true;
            }

            return EncodingDecodeResult.Empty;
        }
    }

    private sealed class MvsSampleCapturedException(MvsSampleCapture capture) : Exception("MVS sample captured.")
    {
        public MvsSampleCapture Capture { get; } = capture;
    }

    private sealed class MvsCaptureDecoder(
        Stream transport,
        string sampleName,
        string outputDirectory,
        int maximumPrefixLength) : IRfbEncodingDecoder
    {
        public int EncodingId => 1011;
        private bool _setupCaptured;

        public async ValueTask<EncodingDecodeResult> DecodeAsync(
            RfbReader reader,
            Framebuffer framebuffer,
            FramebufferRect rectangle,
            CancellationToken cancellationToken)
        {
            var capturedRect = new CapturedRectangle(
                checked((ushort)rectangle.X),
                checked((ushort)rectangle.Y),
                checked((ushort)rectangle.Width),
                checked((ushort)rectangle.Height));

            Console.WriteLine(
                $"MvsCandidateObserved=True Rectangle={capturedRect.Width}x{capturedRect.Height} at ({capturedRect.X},{capturedRect.Y})");

            var prefixBuffer = new byte[maximumPrefixLength];
            var first4 = await reader.ReadBytesAsync(4, cancellationToken).ConfigureAwait(false);
            first4.CopyTo(prefixBuffer, 0);
            var totalRead = 4;

            uint? heuristicLength = null;
            var candidateLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(first4);
            var isNalu = first4[0] == 0 && first4[1] == 0 && (first4[2] == 1 || (first4[2] == 0 && first4[3] == 1));
            if (!isNalu && candidateLength is > 0 and <= 100 * 1024 * 1024)
            {
                heuristicLength = candidateLength;
            }

            if (heuristicLength.HasValue)
            {
                var remainingToRead = (int)Math.Min((long)heuristicLength.Value, maximumPrefixLength - 4);
                if (remainingToRead > 0)
                {
                    var rest = await reader.ReadBytesAsync(remainingToRead, cancellationToken).ConfigureAwait(false);
                    rest.CopyTo(prefixBuffer, 4);
                    totalRead += remainingToRead;
                }
            }
            else
            {
                var tempBuf = new byte[4096];
                while (totalRead < maximumPrefixLength)
                {
                    using var quickCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    quickCts.CancelAfter(TimeSpan.FromMilliseconds(300));
                    try
                    {
                        var toRead = Math.Min(tempBuf.Length, maximumPrefixLength - totalRead);
                        var bytesRead = await transport.ReadAsync(tempBuf.AsMemory(0, toRead), quickCts.Token).ConfigureAwait(false);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        Buffer.BlockCopy(tempBuf, 0, prefixBuffer, totalRead, bytesRead);
                        totalRead += bytesRead;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            var payloadPrefix = new byte[totalRead];
            Buffer.BlockCopy(prefixBuffer, 0, payloadPrefix, 0, totalRead);

            var signature = MvsSampleCaptureReader.DetectHeuristicSignature(payloadPrefix);

            var capture = new MvsSampleCapture(
                SchemaVersion: 1,
                EncodingId: 1011,
                SampleName: sampleName,
                Rectangle: capturedRect,
                PrefixLength: totalRead,
                PayloadSha256: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payloadPrefix)),
                PayloadPrefix: payloadPrefix,
                HeuristicBigEndianLength: heuristicLength,
                HeuristicSignature: signature,
                TimestampUtc: DateTimeOffset.UtcNow);

            if (capturedRect.Width == 0 && capturedRect.Height == 0 && !_setupCaptured)
            {
                _setupCaptured = true;
                var setupDir = Path.Combine(outputDirectory, "setup");
                await MvsSampleCaptureReader.WriteSampleAsync(setupDir, capture, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"MvsSetupCaptured=True PrefixLength={totalRead} SHA256={capture.PayloadSha256}");
                return EncodingDecodeResult.Empty;
            }

            throw new MvsSampleCapturedException(capture);
        }
    }

    internal async Task ProbeScaleAsync(double scale, CancellationToken cancellationToken)
    {
        if (scale is not (0.5d or 1d))
            throw new ArgumentOutOfRangeException(nameof(scale));
        lock (_lifecycleSync)
        {
            ThrowIfProtocolUnavailableNoLock();
            if (!_bootstrapConfigured || !_bootstrapState.IsFirstPixelConfirmed ||
                _handshake?.Version != RfbVersion.V3_889 || !_transport.IsEncrypted ||
                _receiveActive || _framebufferRequestOutstanding || _qualityTransitionActive)
                throw new InvalidOperationException("Scale probe requires an encrypted idle pixel-confirmed ARD session.");
            _qualityTransitionActive = true;
        }
        try
        {
            await _messageScheduler.EnqueueBackgroundAsync(
                token => new ArdClientMessageWriter(new RfbWriter(_transport))
                    .WriteScalingFactorAsync(scale, token), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            BeginShutdown();
            throw;
        }
        finally
        {
            lock (_lifecycleSync) _qualityTransitionActive = false;
        }
    }
}

