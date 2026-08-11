using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using WinARD.ProtocolProbe;
using WinARD.ProtocolProbe.EncodingResearch;
using WinARD.ProtocolProbe.RdmCapture;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Errors;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Tools;

public sealed class EncodingPrefixCaptureTests
{
    private const int CandidateEncoding = 12345;

    [Fact]
    public async Task Reader_captures_candidate_rectangle_length_and_payload_prefix()
    {
        var payload = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        await using var stream = new MemoryStream(CreateUpdate(CandidateEncoding, payload));

        var capture = await EncodingPrefixReader.ReadAsync(
            stream,
            CandidateEncoding,
            64 * 1024,
            CancellationToken.None);

        Assert.Equal(1, capture.SchemaVersion);
        Assert.Equal(CandidateEncoding, capture.EncodingId);
        Assert.Equal(new CapturedRectangle(0, 0, 1920, 1080), capture.Rectangle);
        Assert.Equal(payload.Length, capture.PrefixLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), capture.PayloadSha256);
        Assert.Equal(payload, capture.PayloadPrefix);
    }

    [Fact]
    public async Task Reader_skips_at_most_eight_empty_updates()
    {
        var bytes = Enumerable.Repeat(new byte[] { 0, 0, 0, 0 }, 8).SelectMany(value => value).ToArray();
        await using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            EncodingPrefixReader.ReadAsync(
                stream,
                CandidateEncoding,
                64 * 1024,
                CancellationToken.None));

        Assert.Contains("8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_rejects_raw_fallback()
    {
        await using var stream = new MemoryStream(CreateUpdate(0, [1, 2, 3, 4], includeLength: false));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            EncodingPrefixReader.ReadAsync(stream, CandidateEncoding, 1024, CancellationToken.None));

        Assert.Contains("Raw", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_rejects_rectangle_count_above_limit()
    {
        await using var stream = new MemoryStream([0, 0, 0x10, 0x01]);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            EncodingPrefixReader.ReadAsync(stream, CandidateEncoding, 1024, CancellationToken.None));

        Assert.Contains("4096", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65537)]
    public async Task Reader_rejects_invalid_prefix_limit(int maximumPrefixLength)
    {
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            EncodingPrefixReader.ReadAsync(
                stream,
                CandidateEncoding,
                maximumPrefixLength,
                CancellationToken.None));
    }

    [Fact]
    public async Task Reader_reads_bounded_payload_prefix_once_and_accepts_a_partial_read()
    {
        var payload = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        await using var stream = new PayloadReadTrackingStream(CreateUpdate(CandidateEncoding, payload), 7);

        var capture = await EncodingPrefixReader.ReadAsync(
            stream,
            CandidateEncoding,
            64,
            CancellationToken.None);

        Assert.Equal(payload[..7], capture.PayloadPrefix);
        Assert.Equal(1, stream.PayloadReadCount);
        Assert.Equal(64, stream.PayloadReadRequestedLength);
    }

    [Fact]
    public async Task Reader_rejects_zero_length_candidate_payload()
    {
        await using var stream = new MemoryStream(CreateUpdate(CandidateEncoding, []));

        var exception = await Assert.ThrowsAsync<EndOfStreamException>(() =>
            EncodingPrefixReader.ReadAsync(stream, CandidateEncoding, 1024, CancellationToken.None));

        Assert.Equal(
            "The candidate encoding rectangle contained no observable payload prefix.",
            exception.Message);
    }

    [Fact]
    public async Task Capture_file_writes_only_safe_manifest_and_payload_without_overwriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-prefix-{Guid.NewGuid():N}");
        var payload = new byte[] { 1, 2, 3, 4 };
        var capture = CreateCapture(payload);
        try
        {
            await EncodingPrefixCaptureFile.WriteAsync(
                directory,
                capture,
                syntheticScreenConfirmed: true,
                CancellationToken.None);

            Assert.Equal(
                ["manifest.json", "payload-prefix.bin"],
                Directory.GetFiles(directory).Select(path => Path.GetFileName(path)!).Order().ToArray());
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(directory, "payload-prefix.bin")));
            var manifest = await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"));
            using var document = JsonDocument.Parse(manifest);
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(CandidateEncoding, root.GetProperty("encodingId").GetInt32());
            Assert.Equal(payload.Length, root.GetProperty("prefixLength").GetInt32());
            Assert.True(root.GetProperty("syntheticScreenConfirmed").GetBoolean());
            Assert.DoesNotContain("payloadPrefix", manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("host", manifest, StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAsync<IOException>(() => EncodingPrefixCaptureFile.WriteAsync(
                directory,
                capture,
                syntheticScreenConfirmed: true,
                CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Capture_file_requires_synthetic_screen_confirmation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EncodingPrefixCaptureFile.WriteAsync(
                "unused",
                CreateCapture([1]),
                syntheticScreenConfirmed: false,
                CancellationToken.None));
    }

    [Fact]
    public async Task Capture_file_rejects_payload_hash_mismatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-prefix-{Guid.NewGuid():N}");
        var capture = CreateCapture([1, 2, 3]) with { PayloadSha256 = new string('0', 64) };
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => EncodingPrefixCaptureFile.WriteAsync(
                directory,
                capture,
                syntheticScreenConfirmed: true,
                CancellationToken.None));
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Runner_rejects_missing_synthetic_screen_confirmation_before_files_or_network()
    {
        using var username = SecretMaterial.FromUtf8("user");
        using var password = SecretMaterial.FromUtf8("password");
        var runner = new EncodingPrefixCaptureRunner();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            "unresolvable.invalid",
            5900,
            username,
            password,
            "missing-baseline.json",
            "missing-adaptive.json",
            "missing-output",
            syntheticScreenConfirmed: false,
            CancellationToken.None));

        Assert.Equal(
            "Encoding payload capture requires an explicit synthetic-screen confirmation.",
            exception.Message);
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 12345, 12346 })]
    public async Task Runner_rejects_non_unique_differential_candidate_before_network(int[] adaptiveOnly)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var baselinePath = Path.Combine(directory, "baseline.json");
        var adaptivePath = Path.Combine(directory, "adaptive.json");
        await RdmCaptureFile.WriteAsync(baselinePath, CreateRdmReport([0]), CancellationToken.None);
        await RdmCaptureFile.WriteAsync(
            adaptivePath,
            CreateRdmReport([0, .. adaptiveOnly]),
            CancellationToken.None);
        using var username = SecretMaterial.FromUtf8("user");
        using var password = SecretMaterial.FromUtf8("password");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new EncodingPrefixCaptureRunner().RunAsync(
                "unresolvable.invalid",
                5900,
                username,
                password,
                baselinePath,
                adaptivePath,
                Path.Combine(directory, "output"),
                syntheticScreenConfirmed: true,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_rejects_output_conflict_before_network()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-conflict-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(directory, "output");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "manifest.json"), "existing");
        using var username = SecretMaterial.FromUtf8("user");
        using var password = SecretMaterial.FromUtf8("password");
        try
        {
            var exception = await Assert.ThrowsAsync<IOException>(() => new EncodingPrefixCaptureRunner().RunAsync(
                "unresolvable.invalid",
                5900,
                username,
                password,
                Path.Combine(directory, "missing-baseline.json"),
                Path.Combine(directory, "missing-adaptive.json"),
                outputDirectory,
                syntheticScreenConfirmed: true,
                CancellationToken.None));
            Assert.Contains("not empty", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_maps_overall_timeout_to_probe_timeout()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var baselinePath = Path.Combine(directory, "baseline.json");
        var adaptivePath = Path.Combine(directory, "adaptive.json");
        await RdmCaptureFile.WriteAsync(baselinePath, CreateRdmReport([0]), CancellationToken.None);
        await RdmCaptureFile.WriteAsync(adaptivePath, CreateRdmReport([0, CandidateEncoding]), CancellationToken.None);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            accepted.TrySetResult();
            var buffer = new byte[1];
            try
            {
                _ = await client.GetStream().ReadAsync(buffer);
            }
            catch (IOException)
            {
            }
        });
        using var username = SecretMaterial.FromUtf8("user");
        using var password = SecretMaterial.FromUtf8("password");
        try
        {
            var exception = await Assert.ThrowsAsync<ProbeTimeoutException>(() =>
                new EncodingPrefixCaptureRunner(TimeSpan.FromMilliseconds(200)).RunAsync(
                    IPAddress.Loopback.ToString(),
                    ((IPEndPoint)listener.LocalEndpoint).Port,
                    username,
                    password,
                    baselinePath,
                    adaptivePath,
                    Path.Combine(directory, "output"),
                    syntheticScreenConfirmed: true,
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Probe timed out.", ProbeOutput.FormatFailure(exception));
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static EncodingPrefixCapture CreateCapture(byte[] payload) =>
        new(
            1,
            CandidateEncoding,
            new CapturedRectangle(0, 0, 1920, 1080),
            payload.Length,
            Convert.ToHexString(SHA256.HashData(payload)),
            payload);

    private static RdmCaptureReport CreateRdmReport(IReadOnlyList<int> encodings) =>
        new(
            1,
            "test-profile",
            "RFB 003.889",
            0xC1,
            new CapturedPixelFormat(32, 24, false, true, 255, 255, 255, 16, 8, 0),
            encodings,
            [],
            true,
            null);

    private static byte[] CreateUpdate(int encodingId, byte[] payload, bool includeLength = true)
    {
        var bytes = new byte[checked(4 + 12 + (includeLength ? 4 : 0) + payload.Length)];
        bytes[3] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), 1920);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), 1080);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), encodingId);
        var offset = 16;
        if (includeLength)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), checked((uint)payload.Length));
            offset += 4;
        }

        payload.CopyTo(bytes, offset);
        return bytes;
    }

    private sealed class PayloadReadTrackingStream(byte[] bytes, int partialPayloadCount) : MemoryStream(bytes)
    {
        public int PayloadReadCount { get; private set; }

        public int PayloadReadRequestedLength { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length >= 64)
            {
                PayloadReadCount++;
                PayloadReadRequestedLength = buffer.Length;
                buffer = buffer[..Math.Min(partialPayloadCount, buffer.Length)];
            }

            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
