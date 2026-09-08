using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using WinARD.ProtocolProbe.EncodingResearch;
using WinARD.Remote.Protocol.Errors;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Tools;

public sealed class MvsSampleCaptureTests
{
    private static byte[] BuildFramebufferUpdate(
        ushort x,
        ushort y,
        ushort width,
        ushort height,
        int encodingId,
        byte[] payload)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x00); // MessageType 0 (FramebufferUpdate)
        stream.WriteByte(0x00); // Padding
        Span<byte> uint16Buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(uint16Buffer, 1); // 1 rectangle
        stream.Write(uint16Buffer);

        BinaryPrimitives.WriteUInt16BigEndian(uint16Buffer, x);
        stream.Write(uint16Buffer);
        BinaryPrimitives.WriteUInt16BigEndian(uint16Buffer, y);
        stream.Write(uint16Buffer);
        BinaryPrimitives.WriteUInt16BigEndian(uint16Buffer, width);
        stream.Write(uint16Buffer);
        BinaryPrimitives.WriteUInt16BigEndian(uint16Buffer, height);
        stream.Write(uint16Buffer);

        Span<byte> int32Buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(int32Buffer, encodingId);
        stream.Write(int32Buffer);

        stream.Write(payload);
        return stream.ToArray();
    }

    [Fact]
    public async Task ReadAsync_captures_1011_rectangle_and_bounded_payload_with_nalu_heuristics()
    {
        byte[] payload = [0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1E, 0x9D];
        var data = BuildFramebufferUpdate(0, 0, 1920, 1080, 1011, payload);
        using var stream = new MemoryStream(data);

        var capture = await MvsSampleCaptureReader.ReadAsync(
            stream,
            candidateEncodingId: 1011,
            sampleName: "test-nalu",
            CancellationToken.None,
            framebufferWidth: 1920,
            framebufferHeight: 1080);

        Assert.Equal(1011, capture.EncodingId);
        Assert.Equal("test-nalu", capture.SampleName);
        Assert.Equal(new CapturedRectangle(0, 0, 1920, 1080), capture.Rectangle);
        Assert.Equal(payload.Length, capture.PrefixLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), capture.PayloadSha256);
        Assert.Equal(payload, capture.PayloadPrefix);
        Assert.Contains("NALU", capture.HeuristicSignature);
    }

    [Fact]
    public async Task ReadAsync_detects_heuristic_zlib_signature()
    {
        byte[] payload = [0x78, 0x9C, 0x01, 0x02, 0x03, 0x04];
        var data = BuildFramebufferUpdate(10, 20, 640, 480, 1011, payload);
        using var stream = new MemoryStream(data);

        var capture = await MvsSampleCaptureReader.ReadAsync(
            stream,
            candidateEncodingId: 1011,
            sampleName: "test-zlib",
            CancellationToken.None);

        Assert.Equal("Zlib Stream", capture.HeuristicSignature);
        Assert.Equal(payload.Length, capture.PrefixLength);
    }

    [Fact]
    public async Task ReadAsync_detects_fourcc_magic()
    {
        byte[] payload = [0x6D, 0x76, 0x73, 0x31, 0x00, 0x01]; // "mvs1"
        var data = BuildFramebufferUpdate(0, 0, 100, 100, 1011, payload);
        using var stream = new MemoryStream(data);

        var capture = await MvsSampleCaptureReader.ReadAsync(
            stream,
            candidateEncodingId: 1011,
            sampleName: "test-magic",
            CancellationToken.None);

        Assert.Equal("FourCC Magic (mvs1)", capture.HeuristicSignature);
    }

    [Fact]
    public async Task ReadAsync_rejects_mismatched_encoding()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        var data = BuildFramebufferUpdate(0, 0, 800, 600, 1002, payload);
        using var stream = new MemoryStream(data);

        var exception = await Assert.ThrowsAsync<EncodingCandidateNotObservedException>(() =>
            MvsSampleCaptureReader.ReadAsync(
                stream,
                candidateEncodingId: 1011,
                sampleName: "mismatch",
                CancellationToken.None));

        Assert.Equal(1002, exception.ObservedEncodingId);
    }

    [Fact]
    public async Task ReadAsync_rejects_zero_dimension_rectangle()
    {
        byte[] payload = [0x01, 0x02];
        var data = BuildFramebufferUpdate(0, 0, 0, 100, 1011, payload);
        using var stream = new MemoryStream(data);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            MvsSampleCaptureReader.ReadAsync(
                stream,
                candidateEncodingId: 1011,
                sampleName: "zero-dim",
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_rejects_out_of_bounds_rectangle()
    {
        byte[] payload = [0x01, 0x02];
        var data = BuildFramebufferUpdate(100, 100, 1920, 1080, 1011, payload);
        using var stream = new MemoryStream(data);

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            MvsSampleCaptureReader.ReadAsync(
                stream,
                candidateEncodingId: 1011,
                sampleName: "out-of-bounds",
                CancellationToken.None,
                framebufferWidth: 1920,
                framebufferHeight: 1080));
    }

    [Fact]
    public async Task ReadAsync_rejects_empty_payload()
    {
        var data = BuildFramebufferUpdate(0, 0, 100, 100, 1011, []);
        using var stream = new MemoryStream(data);

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            MvsSampleCaptureReader.ReadAsync(
                stream,
                candidateEncodingId: 1011,
                sampleName: "empty-payload",
                CancellationToken.None));
    }

    [Fact]
    public async Task WriteSampleAsync_persists_manifest_and_payload_bin_matching_hashes()
    {
        byte[] payload = [0x11, 0x22, 0x33, 0x44, 0x55];
        var capture = new MvsSampleCapture(
            SchemaVersion: 1,
            EncodingId: 1011,
            SampleName: "solid-blue",
            Rectangle: new CapturedRectangle(0, 0, 1920, 1080),
            PrefixLength: payload.Length,
            PayloadSha256: Convert.ToHexString(SHA256.HashData(payload)),
            PayloadPrefix: payload,
            HeuristicBigEndianLength: null,
            HeuristicSignature: "Unknown / Raw Payload Header",
            TimestampUtc: DateTimeOffset.UtcNow);

        var tempDir = Path.Combine(Path.GetTempPath(), $"WinARD-mvs-sample-test-{Guid.NewGuid():N}");
        try
        {
            await MvsSampleCaptureReader.WriteSampleAsync(tempDir, capture, CancellationToken.None);

            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var payloadPath = Path.Combine(tempDir, "payload-prefix.bin");

            Assert.True(File.Exists(manifestPath));
            Assert.True(File.Exists(payloadPath));

            var readPayload = await File.ReadAllBytesAsync(payloadPath);
            Assert.Equal(payload, readPayload);

            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            using var doc = JsonDocument.Parse(manifestJson);
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(1011, root.GetProperty("encodingId").GetInt32());
            Assert.Equal("solid-blue", root.GetProperty("sampleName").GetString());
            Assert.Equal(capture.PayloadSha256, root.GetProperty("payloadSha256").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void MvsSampleCapture_deserializes_legacy_manifest_without_new_fields_safely()
    {
        // Legacy manifest generated before completeness / declaredLength / actualLength were added
        var legacyJson = """
        {
          "schemaVersion": 1,
          "encodingId": 1011,
          "sampleName": "solid-blue",
          "rectangle": {
            "x": 3360,
            "y": 0,
            "width": 6016,
            "height": 3384
          },
          "prefixLength": 65536,
          "payloadSha256": "98E95F38EB7ACFA7725C6ACBA0D1BE14689A47FB9ABB464A56DC5D2AE1A76D18",
          "payloadPrefix": "AAA=",
          "heuristicBigEndianLength": 364783,
          "heuristicSignature": "Unknown / Raw Payload Header",
          "timestampUtc": "2026-09-06T15:20:00+00:00"
        }
        """;

        var capture = JsonSerializer.Deserialize<MvsSampleCapture>(legacyJson, CaseInsensitiveJsonOptions);

        Assert.NotNull(capture);
        Assert.Equal(1011, capture.EncodingId);
        Assert.Equal("solid-blue", capture.SampleName);
        Assert.Equal(MvsRecordCompleteness.PrefixOnly, capture.Completeness);
        Assert.Equal(MvsRecordKind.Unknown, capture.RecordKind);
        Assert.Null(capture.DeclaredLength);
        Assert.Null(capture.ActualLength);
        Assert.Null(capture.SuccessorMessageType);
        Assert.Null(capture.SuccessorEncodingId);
        Assert.True(capture.IsIncomplete);
    }

    [Fact]
    public async Task WriteSampleAsync_detects_hash_mismatch_and_cleans_up_staging()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        var capture = new MvsSampleCapture(
            SchemaVersion: 1,
            EncodingId: 1011,
            SampleName: "corrupt-hash",
            Rectangle: new CapturedRectangle(0, 0, 100, 100),
            PrefixLength: payload.Length,
            PayloadSha256: "WRONG_HASH_00000000000000000000000000000000000000000000000000000000",
            PayloadPrefix: payload,
            HeuristicBigEndianLength: null,
            HeuristicSignature: "test",
            TimestampUtc: DateTimeOffset.UtcNow);

        var tempDir = Path.Combine(Path.GetTempPath(), $"WinARD-test-mismatch-{Guid.NewGuid():N}");
        var parentDir = Path.GetDirectoryName(tempDir)!;

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                MvsSampleCaptureReader.WriteSampleAsync(tempDir, capture, CancellationToken.None));
            Assert.Contains("Payload hash mismatch", ex.Message);
            Assert.False(Directory.Exists(tempDir));

            // Staging directory must be cleaned up
            var stagingDirs = Directory.GetDirectories(parentDir, ".staging-*");
            Assert.Empty(stagingDirs);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_enforces_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var stream = new MemoryStream([0x00, 0x00]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MvsSampleCaptureReader.ReadAsync(
                stream,
                candidateEncodingId: 1011,
                sampleName: "cancelled",
                cts.Token));
    }

    private static byte[] BuildMvsPayload(byte[] content)
    {
        var buf = new byte[4 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)content.Length);
        Buffer.BlockCopy(content, 0, buf, 4, content.Length);
        return buf;
    }

    private static byte[] BuildMultiRectangleFramebufferUpdate(
        (ushort x, ushort y, ushort width, ushort height, int encodingId, byte[] payload)[] rectangles)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x00); // MessageType 0 (FramebufferUpdate)
        stream.WriteByte(0x00); // Padding
        Span<byte> uint16Buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(uint16Buffer, (ushort)rectangles.Length);
        stream.Write(uint16Buffer);

        Span<byte> int32Buffer = stackalloc byte[4];
        foreach (var rect in rectangles)
        {
            BinaryPrimitives.WriteUInt16BigEndian(uint16Buffer, rect.x);
            stream.Write(uint16Buffer);
            BinaryPrimitives.WriteUInt16BigEndian(uint16Buffer, rect.y);
            stream.Write(uint16Buffer);
            BinaryPrimitives.WriteUInt16BigEndian(uint16Buffer, rect.width);
            stream.Write(uint16Buffer);
            BinaryPrimitives.WriteUInt16BigEndian(uint16Buffer, rect.height);
            stream.Write(uint16Buffer);

            BinaryPrimitives.WriteInt32BigEndian(int32Buffer, rect.encodingId);
            stream.Write(int32Buffer);

            stream.Write(rect.payload);
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task ReadCompleteRecordAsync_captures_full_payload_and_validates_next_rectangle_boundary()
    {
        byte[] content = [0x01, 0x02, 0x03, 0x04, 0x05];
        var mvsPayload = BuildMvsPayload(content);

        var updateData = BuildMultiRectangleFramebufferUpdate([
            (0, 0, 1920, 1080, 1011, mvsPayload),
            (0, 0, 10, 10, 0, [0xAA, 0xBB]) // next rectangle with raw encoding 0
        ]);

        using var stream = new MemoryStream(updateData);
        var capture = await MvsSampleCaptureReader.ReadCompleteRecordAsync(
            stream,
            candidateEncodingId: 1011,
            sampleName: "test-next-rect",
            CancellationToken.None,
            validateSuccessorBoundary: true);

        Assert.Equal(1011, capture.EncodingId);
        Assert.Equal("test-next-rect", capture.SampleName);
        Assert.Equal(content.Length, capture.ActualLength);
        Assert.Equal((uint)content.Length, capture.DeclaredLength);
        Assert.Equal(MvsRecordCompleteness.SuccessorBoundaryValidated, capture.Completeness);
        Assert.Equal(0, capture.SuccessorEncodingId);
        Assert.Null(capture.SuccessorMessageType);
        Assert.Equal(MvsRecordKind.ImageSlice, capture.RecordKind);
        Assert.Equal(mvsPayload, capture.PayloadPrefix);
    }

    [Fact]
    public async Task ReadCompleteRecordAsync_captures_control_setup_and_validates_next_message_boundary()
    {
        byte[] content = new byte[129];
        content[0] = 0x02; // mode
        var mvsPayload = BuildMvsPayload(content);

        var updateData = BuildFramebufferUpdate(0, 0, 0, 0, 1011, mvsPayload);
        using var stream = new MemoryStream();
        stream.Write(updateData);
        stream.WriteByte(0x02); // Next server message: Bell (type 2)
        stream.Position = 0;

        var capture = await MvsSampleCaptureReader.ReadCompleteRecordAsync(
            stream,
            candidateEncodingId: 1011,
            sampleName: "test-setup-msg",
            CancellationToken.None,
            validateSuccessorBoundary: true);

        Assert.Equal(MvsRecordKind.ControlSetup, capture.RecordKind);
        Assert.Equal(129, capture.ActualLength);
        Assert.Equal((uint)129, capture.DeclaredLength);
        Assert.Equal(MvsRecordCompleteness.SuccessorBoundaryValidated, capture.Completeness);
        Assert.Equal((byte)2, capture.SuccessorMessageType);
        Assert.Null(capture.SuccessorEncodingId);
    }

    [Fact]
    public async Task ReadCompleteRecordAsync_handles_chunked_stream_delivery()
    {
        byte[] content = new byte[500];
        for (var i = 0; i < content.Length; i++) content[i] = (byte)(i % 256);
        var mvsPayload = BuildMvsPayload(content);

        var updateData = BuildFramebufferUpdate(0, 0, 100, 100, 1011, mvsPayload);
        using var rawStream = new MemoryStream();
        rawStream.Write(updateData);
        rawStream.WriteByte(0x00); // next message type 0
        rawStream.Position = 0;

        using var chunkedStream = new ChunkedReadStream(rawStream, maxChunkSize: 7);

        var capture = await MvsSampleCaptureReader.ReadCompleteRecordAsync(
            chunkedStream,
            candidateEncodingId: 1011,
            sampleName: "test-chunked",
            CancellationToken.None,
            validateSuccessorBoundary: true);

        Assert.Equal(500, capture.ActualLength);
        Assert.Equal(MvsRecordCompleteness.SuccessorBoundaryValidated, capture.Completeness);
        Assert.Equal((byte)0, capture.SuccessorMessageType);
    }

    [Fact]
    public async Task ReadCompleteRecordAsync_rejects_desynchronized_successor_rectangle()
    {
        byte[] content = [0x01, 0x02];
        var mvsPayload = BuildMvsPayload(content);

        var updateData = BuildMultiRectangleFramebufferUpdate([
            (0, 0, 100, 100, 1011, mvsPayload),
            (0, 0, 10, 10, 999999, [0x00]) // unexpected encoding ID
        ]);

        using var stream = new MemoryStream(updateData);
        var exception = await Assert.ThrowsAsync<MvsBoundaryDesynchronizedException>(() =>
            MvsSampleCaptureReader.ReadCompleteRecordAsync(
                stream,
                candidateEncodingId: 1011,
                sampleName: "desync-rect",
                CancellationToken.None,
                validateSuccessorBoundary: true));

        Assert.Contains("unexpected encoding ID 999999", exception.Message);
    }

    [Fact]
    public async Task ReadCompleteRecordAsync_rejects_desynchronized_successor_message()
    {
        byte[] content = [0x01, 0x02];
        var mvsPayload = BuildMvsPayload(content);

        var updateData = BuildFramebufferUpdate(0, 0, 100, 100, 1011, mvsPayload);
        using var stream = new MemoryStream();
        stream.Write(updateData);
        stream.WriteByte(0x7F); // Invalid server message type
        stream.Position = 0;

        var exception = await Assert.ThrowsAsync<MvsBoundaryDesynchronizedException>(() =>
            MvsSampleCaptureReader.ReadCompleteRecordAsync(
                stream,
                candidateEncodingId: 1011,
                sampleName: "desync-msg",
                CancellationToken.None,
                validateSuccessorBoundary: true));

        Assert.Contains("unexpected message type 127", exception.Message);
    }

    [Fact]
    public async Task ReadCompleteRecordAsync_rejects_payload_exceeding_quota()
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x00);
        stream.WriteByte(0x00);
        var u16 = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(u16, 1);
        stream.Write(u16); // 1 rect
        BinaryPrimitives.WriteUInt16BigEndian(u16, 0);
        stream.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 0);
        stream.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 100);
        stream.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 100);
        stream.Write(u16);
        var i32 = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(i32, 1011);
        stream.Write(i32);

        // declaredLength = 16 MiB + 1
        BinaryPrimitives.WriteUInt32BigEndian(i32, 16 * 1024 * 1024 + 1);
        stream.Write(i32);
        stream.Position = 0;

        var ex = await Assert.ThrowsAsync<MvsQuotaExceededException>(() =>
            MvsSampleCaptureReader.ReadCompleteRecordAsync(
                stream,
                candidateEncodingId: 1011,
                sampleName: "quota-exceeded",
                CancellationToken.None));

        Assert.Contains("exceeds the single-record limit", ex.Message);
    }

    [Fact]
    public async Task ReadCompleteRecordAsync_rejects_truncated_payload()
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x00);
        stream.WriteByte(0x00);
        var u16 = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(u16, 1);
        stream.Write(u16); // 1 rect
        BinaryPrimitives.WriteUInt16BigEndian(u16, 0);
        stream.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 0);
        stream.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 100);
        stream.Write(u16);
        BinaryPrimitives.WriteUInt16BigEndian(u16, 100);
        stream.Write(u16);
        var i32 = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(i32, 1011);
        stream.Write(i32);

        // declaredLength = 100, but only 10 bytes provided
        BinaryPrimitives.WriteUInt32BigEndian(i32, 100);
        stream.Write(i32);
        stream.Write(new byte[10]);
        stream.Position = 0;

        var ex = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            MvsSampleCaptureReader.ReadCompleteRecordAsync(
                stream,
                candidateEncodingId: 1011,
                sampleName: "truncated",
                CancellationToken.None));
        Assert.IsType<EndOfStreamException>(ex.InnerException);
    }

    [Fact]
    public async Task WriteSampleAsync_rejects_existing_non_empty_directory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinARD-test-nonempty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "existing.txt"), "hello");

        var capture = new MvsSampleCapture(
            SchemaVersion: 1,
            EncodingId: 1011,
            SampleName: "test-overwrite",
            Rectangle: new CapturedRectangle(0, 0, 100, 100),
            PrefixLength: 4,
            PayloadSha256: "abcd",
            PayloadPrefix: [0x01, 0x02, 0x03, 0x04],
            HeuristicBigEndianLength: null,
            HeuristicSignature: "test",
            TimestampUtc: DateTimeOffset.UtcNow);

        try
        {
            var ex = await Assert.ThrowsAsync<IOException>(() =>
                MvsSampleCaptureReader.WriteSampleAsync(tempDir, capture, CancellationToken.None));
            Assert.Contains("already exists and is not empty", ex.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteSampleAsync_rejects_path_traversal()
    {
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"WinARD-test-root-{Guid.NewGuid():N}");
        var escapedDir = Path.Combine(Path.GetTempPath(), $"WinARD-test-escape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(allowedRoot);

        var capture = new MvsSampleCapture(
            SchemaVersion: 1,
            EncodingId: 1011,
            SampleName: "test-escape",
            Rectangle: new CapturedRectangle(0, 0, 100, 100),
            PrefixLength: 4,
            PayloadSha256: "abcd",
            PayloadPrefix: [0x01, 0x02, 0x03, 0x04],
            HeuristicBigEndianLength: null,
            HeuristicSignature: "test",
            TimestampUtc: DateTimeOffset.UtcNow);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                MvsSampleCaptureReader.WriteSampleAsync(escapedDir, capture, CancellationToken.None, allowedRootDirectory: allowedRoot));
            Assert.Contains("escapes the allowed root directory", ex.Message);
        }
        finally
        {
            if (Directory.Exists(allowedRoot)) Directory.Delete(allowedRoot, recursive: true);
            if (Directory.Exists(escapedDir)) Directory.Delete(escapedDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadCompleteRecordAsync_captures_live_macroblock_16x16_slice_sample()
    {
        // Actual 37-byte live capture payload from genuine Mac 1011 session
        byte[] livePayload = [
            0x00, 0x00, 0x00, 0x21, // declared length = 33
            0x00, 0x0F, 0x19, 0x00, 0x00, 0x09, 0x58, 0xC3, 0x68, 0x1A, 0x78, 0xFD,
            0x11, 0x8C, 0x44, 0x58, 0x12, 0xCC, 0x64, 0x07, 0x62, 0x31, 0x3A, 0x59,
            0x22, 0x26, 0x46, 0x4F, 0x2C, 0x86, 0xB6, 0xB2, 0x6D
        ];

        var updateData = BuildFramebufferUpdate(0, 0, 16, 16, 1011, livePayload);
        using var stream = new MemoryStream();
        stream.Write(updateData);
        stream.WriteByte(0x00); // Successor message: FramebufferUpdate (type 0)
        stream.Position = 0;

        var capture = await MvsSampleCaptureReader.ReadCompleteRecordAsync(
            stream,
            candidateEncodingId: 1011,
            sampleName: "live-slice-16x16",
            CancellationToken.None,
            validateSuccessorBoundary: true);

        Assert.Equal(1011, capture.EncodingId);
        Assert.Equal(new CapturedRectangle(0, 0, 16, 16), capture.Rectangle);
        Assert.Equal(33, capture.ActualLength);
        Assert.Equal((uint)33, capture.DeclaredLength);
        Assert.Equal(MvsRecordCompleteness.SuccessorBoundaryValidated, capture.Completeness);
        Assert.Equal((byte)0, capture.SuccessorMessageType);
        Assert.Equal("4E90E93E4786D90A21B9A1764A22ADE42A1BEA235F9F347E1FC39D1789C8B72B", capture.PayloadSha256);
        Assert.False(capture.IsIncomplete);
    }

    private sealed class ChunkedReadStream(Stream inner, int maxChunkSize) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, maxChunkSize));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            await inner.ReadAsync(buffer[..Math.Min(buffer.Length, maxChunkSize)], cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
