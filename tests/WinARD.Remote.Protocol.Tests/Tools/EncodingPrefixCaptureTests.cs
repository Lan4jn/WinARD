using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinARD.ProtocolProbe;
using WinARD.ProtocolProbe.EncodingResearch;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Handshake;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Tools;

public sealed class EncodingPrefixCaptureTests
{
    private const int CandidateEncoding = 1002;

    [Fact]
    public void Capture_snapshots_the_constructor_payload()
    {
        var source = new byte[] { 1, 2, 3, 4 };
        var capture = CreateCapture(source);

        source[0] = 99;

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, capture.PayloadPrefix);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })),
            capture.PayloadSha256);
    }

    [Fact]
    public void Capture_payload_getter_does_not_expose_internal_storage()
    {
        var capture = CreateCapture([1, 2, 3, 4]);
        var exposed = capture.PayloadPrefix;

        exposed[0] = 99;

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, capture.PayloadPrefix);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(capture.PayloadPrefix)),
            capture.PayloadSha256);
    }

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
    public async Task Reader_reports_stable_candidate_not_observed_category_and_encoding()
    {
        await using var stream = new MemoryStream(
            CreateUpdate(0, [1, 2, 3, 4], includeLength: false, width: 0, height: 0));

        var exception = await Assert.ThrowsAsync<EncodingCandidateNotObservedException>(() =>
            EncodingPrefixReader.ReadAsync(stream, CandidateEncoding, 1024, CancellationToken.None));

        Assert.Equal("candidate-not-observed", exception.Category);
        Assert.Equal(0, exception.ObservedEncodingId);
        Assert.Equal(16, stream.Position);
    }

    [Fact]
    public async Task Reader_rejects_rectangle_count_above_limit()
    {
        await using var stream = new MemoryStream([0, 0, 0x10, 0x01]);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            EncodingPrefixReader.ReadAsync(stream, CandidateEncoding, 1024, CancellationToken.None));

        Assert.Contains("4096", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_accepts_rectangle_count_at_limit_when_first_rectangle_is_candidate()
    {
        var payload = new byte[] { 1, 2, 3 };
        var bytes = CreateUpdate(CandidateEncoding, payload);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 4096);
        await using var stream = new MemoryStream(bytes);

        var capture = await EncodingPrefixReader.ReadAsync(
            stream,
            CandidateEncoding,
            64 * 1024,
            CancellationToken.None);

        Assert.Equal(payload, capture.PayloadPrefix);
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
    public async Task Reader_bounds_uint_max_payload_declaration_to_one_64_kib_read()
    {
        var bytes = CreateUpdate(CandidateEncoding, Enumerable.Range(1, 7).Select(value => (byte)value).ToArray());
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), uint.MaxValue);
        await using var stream = new PayloadReadTrackingStream(bytes, 7);

        var capture = await EncodingPrefixReader.ReadAsync(
            stream,
            CandidateEncoding,
            64 * 1024,
            CancellationToken.None);

        Assert.Equal(7, capture.PrefixLength);
        Assert.Equal(uint.MaxValue, capture.DeclaredPayloadLength);
        Assert.Equal(1, stream.PayloadReadCount);
        Assert.Equal(64 * 1024, stream.PayloadReadRequestedLength);
    }

    [Fact]
    public async Task Reader_propagates_pre_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream(CreateUpdate(CandidateEncoding, [1]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EncodingPrefixReader.ReadAsync(
            stream,
            CandidateEncoding,
            64 * 1024,
            cancellation.Token));
    }

    [Fact]
    public async Task Reader_propagates_cancellation_during_payload_read()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new CancelDuringPayloadStream(
            CreateUpdate(CandidateEncoding, Enumerable.Repeat((byte)1, 64).ToArray()),
            cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EncodingPrefixReader.ReadAsync(
            stream,
            CandidateEncoding,
            64 * 1024,
            cancellation.Token));
    }

    [Fact]
    public async Task Reader_requests_after_only_the_first_seven_of_eight_empty_updates()
    {
        var bytes = Enumerable.Repeat(new byte[] { 0, 0, 0, 0 }, 8).SelectMany(value => value).ToArray();
        await using var stream = new MemoryStream(bytes);
        var requests = 0;

        await Assert.ThrowsAsync<RfbProtocolException>(() => EncodingPrefixReader.ReadAsync(
            stream,
            CandidateEncoding,
            64 * 1024,
            _ =>
            {
                requests++;
                return Task.CompletedTask;
            },
            CancellationToken.None));

        Assert.Equal(7, requests);
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
    public async Task Reader_rejects_zero_sized_candidate_rectangle()
    {
        await using var stream = new MemoryStream(
            CreateUpdate(CandidateEncoding, [1], width: 0, height: 1));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            EncodingPrefixReader.ReadAsync(stream, CandidateEncoding, 1024, CancellationToken.None));

        Assert.Contains("non-zero", exception.Message, StringComparison.Ordinal);
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
            Assert.Equal((uint)payload.Length, root.GetProperty("declaredPayloadLength").GetUInt32());
            Assert.Equal(payload.Length, root.GetProperty("observedPrefixLength").GetInt32());
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
    public async Task Capture_file_creates_independent_bounded_sample_directories()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-prefix-set-{Guid.NewGuid():N}");
        try
        {
            foreach (var sample in EncodingPrefixCaptureFile.RequiredSampleNames)
            {
                await EncodingPrefixCaptureFile.WriteSampleAsync(
                    directory,
                    CandidateEncoding,
                    sample,
                    CreateCapture([1, 2, 3, 4]),
                    syntheticScreenConfirmed: true,
                    CancellationToken.None);
            }

            Assert.Equal(
                EncodingPrefixCaptureFile.RequiredSampleNames.Order(),
                Directory.GetDirectories(directory)
                    .Select(Path.GetFileName)
                    .Order());
            foreach (var manifestPath in Directory.GetFiles(directory, "manifest.json", SearchOption.AllDirectories))
            {
                var manifest = await File.ReadAllTextAsync(manifestPath);
                Assert.DoesNotContain("host", manifest, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("user", manifest, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("password", manifest, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(directory, manifest, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
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
            CandidateEncoding,
            "missing-output",
            syntheticScreenConfirmed: false,
            CancellationToken.None));

        Assert.Equal(
            "Encoding payload capture requires an explicit synthetic-screen confirmation.",
            exception.Message);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1003)]
    public async Task Runner_rejects_non_allowlisted_candidate_before_network(int candidate)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-runner-{Guid.NewGuid():N}");
        using var username = SecretMaterial.FromUtf8("user");
        using var password = SecretMaterial.FromUtf8("password");
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new EncodingPrefixCaptureRunner().RunAsync(
                "unresolvable.invalid",
                5900,
                username,
                password,
                candidate,
                Path.Combine(directory, "output"),
                syntheticScreenConfirmed: true,
                CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
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
                CandidateEncoding,
                outputDirectory,
                syntheticScreenConfirmed: true,
                CancellationToken.None));
            Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_removes_private_staging_when_sample_confirmation_fails()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-staging-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(directory, "output");
        using var username = SecretMaterial.FromUtf8("user");
        using var password = SecretMaterial.FromUtf8("password");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new EncodingPrefixCaptureRunner(
                    TimeSpan.FromSeconds(5),
                    (_, _) => throw new InvalidOperationException("sample not confirmed")).RunAsync(
                    "unresolvable.invalid",
                    5900,
                    username,
                    password,
                    CandidateEncoding,
                    outputDirectory,
                    syntheticScreenConfirmed: true,
                    CancellationToken.None));

            Assert.False(Directory.Exists(outputDirectory));
            Assert.Empty(Directory.GetDirectories(directory, "output.staging-*"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_maps_overall_timeout_to_probe_timeout()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-timeout-{Guid.NewGuid():N}");
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
                new EncodingPrefixCaptureRunner(
                    TimeSpan.FromMilliseconds(200),
                    (_, token) => Task.Delay(TimeSpan.FromMilliseconds(300), token)).RunAsync(
                    IPAddress.Loopback.ToString(),
                    ((IPEndPoint)listener.LocalEndpoint).Port,
                    username,
                    password,
                    CandidateEncoding,
                    Path.Combine(directory, "output"),
                    syntheticScreenConfirmed: true,
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Probe timed out.", ProbeOutput.FormatFailure(exception));
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_cancels_sample_confirmation_without_network_or_artifacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-confirm-cancel-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(directory, "output");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        var confirmationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var username = SecretMaterial.FromUtf8("user");
        using var password = SecretMaterial.FromUtf8("password");
        try
        {
            var runTask = new EncodingPrefixCaptureRunner(
                TimeSpan.FromMilliseconds(100),
                async (_, token) =>
                {
                    confirmationStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }).RunAsync(
                IPAddress.Loopback.ToString(),
                ((IPEndPoint)listener.LocalEndpoint).Port,
                username,
                password,
                CandidateEncoding,
                outputDirectory,
                syntheticScreenConfirmed: true,
                cancellation.Token);

            await confirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
            Assert.False(listener.Pending());
            Assert.False(Directory.Exists(outputDirectory));
            Assert.Empty(Directory.GetDirectories(directory, "output.staging-*"));
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_completes_encrypted_loopback_capture_in_protocol_order()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-success-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(directory, "output");
        var expectedPrefix = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var hardTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var serverTask = RunEncryptedCaptureServerAsync(
            listener,
            CandidateEncoding,
            expectedPrefix,
            hardTimeout.Token);
        using var username = SecretMaterial.FromUtf8("synthetic-user");
        using var password = SecretMaterial.FromUtf8("synthetic-password");
        try
        {
            var confirmedSamples = new List<string>();
            var runTask = new EncodingPrefixCaptureRunner(
                TimeSpan.FromSeconds(5),
                (sample, _) => { confirmedSamples.Add(sample); return Task.CompletedTask; }).RunAsync(
                IPAddress.Loopback.ToString(),
                ((IPEndPoint)listener.LocalEndpoint).Port,
                username,
                password,
                CandidateEncoding,
                outputDirectory,
                syntheticScreenConfirmed: true,
                hardTimeout.Token);

            await Task.WhenAll(serverTask, runTask).WaitAsync(TimeSpan.FromSeconds(10));
            var captures = await runTask;
            var capture = Assert.Single(captures.DistinctBy(item => item.PayloadSha256));

            Assert.Equal(EncodingPrefixCaptureFile.RequiredSampleNames.Count, await serverTask);
            Assert.Equal(EncodingPrefixCaptureFile.RequiredSampleNames, confirmedSamples);
            Assert.Equal(CandidateEncoding, capture.EncodingId);
            Assert.Equal(new CapturedRectangle(0, 0, 4, 2), capture.Rectangle);
            Assert.Equal(expectedPrefix, capture.PayloadPrefix);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expectedPrefix)), capture.PayloadSha256);
            Assert.Equal(
                EncodingPrefixCaptureFile.RequiredSampleNames.Order(),
                Directory.GetDirectories(outputDirectory)
                    .Select(Path.GetFileName)
                    .Order());
            Assert.Equal(
                expectedPrefix,
                await File.ReadAllBytesAsync(
                    Path.Combine(outputDirectory, "solid-color", "payload-prefix.bin"),
                    hardTimeout.Token));
            var manifest = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, "solid-color", "manifest.json"),
                hardTimeout.Token);
            Assert.Contains(capture.PayloadSha256, manifest, StringComparison.Ordinal);
            var setManifest = await File.ReadAllTextAsync(
                Path.Combine(outputDirectory, "capture-set.json"),
                hardTimeout.Token);
            Assert.Contains("\"status\": \"complete\"", setManifest, StringComparison.Ordinal);
            foreach (var sampleName in EncodingPrefixCaptureFile.RequiredSampleNames)
            {
                Assert.Contains(sampleName, setManifest, StringComparison.Ordinal);
            }
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
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

    private static byte[] CreateUpdate(
        int encodingId,
        byte[] payload,
        bool includeLength = true,
        ushort width = 1920,
        ushort height = 1080)
    {
        var bytes = new byte[checked(4 + 12 + (includeLength ? 4 : 0) + payload.Length)];
        bytes[3] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), height);
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

    private static async Task<int> RunEncryptedCaptureServerAsync(
        TcpListener listener,
        int candidateEncodingId,
        byte[] payloadPrefix,
        CancellationToken cancellationToken)
    {
        var activationRequests = 0;
        foreach (var _ in EncodingPrefixCaptureFile.RequiredSampleNames)
        {
            activationRequests += await RunEncryptedCaptureSessionAsync(
                listener,
                candidateEncodingId,
                payloadPrefix,
                cancellationToken);
        }

        return activationRequests;
    }

    private static async Task<int> RunEncryptedCaptureSessionAsync(
        TcpListener listener,
        int candidateEncodingId,
        byte[] payloadPrefix,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var banner = Encoding.ASCII.GetBytes("RFB 003.889\n");
        await stream.WriteAsync(banner, cancellationToken);
        Assert.Equal(banner, await ReadExactlyAsync(stream, banner.Length, cancellationToken));
        await stream.WriteAsync(
            new byte[] { 1, (byte)RfbSecurityType.AppleRemoteDesktop },
            cancellationToken);
        Assert.Equal(
            new byte[] { (byte)RfbSecurityType.AppleRemoteDesktop },
            await ReadExactlyAsync(stream, 1, cancellationToken));

        var modulus = Convert.FromHexString(
            "D2652EF10104A3DDC1219700EDFBD1E19F7678B4A4F6D5952634BD8BF1D60326322B5D32366DC25CB4E8E73AF4312A70D2DCAF2747EB89D7E88553EECD6A283D");
        var prime = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var serverPublic = ToFixedWidth(
            BigInteger.ModPow(new BigInteger(5), new BigInteger(3), prime),
            modulus.Length);
        var challenge = new byte[4 + (2 * modulus.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(challenge, 5);
        BinaryPrimitives.WriteUInt16BigEndian(challenge.AsSpan(2), checked((ushort)modulus.Length));
        modulus.CopyTo(challenge, 4);
        serverPublic.CopyTo(challenge, 4 + modulus.Length);
        await stream.WriteAsync(challenge, cancellationToken);
        var response = await ReadExactlyAsync(stream, 128 + modulus.Length, cancellationToken);
        var clientPublic = new BigInteger(response.AsSpan(128), isUnsigned: true, isBigEndian: true);
        var sharedSecret = ToFixedWidth(
            BigInteger.ModPow(clientPublic, new BigInteger(3), prime),
            modulus.Length);
        byte[] authenticationKey;
#pragma warning disable CA5351 // MD5 is mandated by ARD security type 30 compatibility.
        authenticationKey = MD5.HashData(sharedSecret);
#pragma warning restore CA5351
        await stream.WriteAsync(new byte[4], cancellationToken);

        Assert.Equal(new byte[] { 0xC1 }, await ReadExactlyAsync(stream, 1, cancellationToken));
        await stream.WriteAsync(CreateExtendedServerInit(4, 2), cancellationToken);
        Assert.Equal(0x21, (await ReadExactlyAsync(stream, 66, cancellationToken))[0]);
        Assert.Equal(
            new byte[] { 0x0A, 0, 0, 1 },
            await ReadExactlyAsync(stream, 4, cancellationToken));
        Assert.Equal(
            new byte[] { 0x0D, 1, 0, 0, 0, 0, 0, 0 },
            await ReadExactlyAsync(stream, 8, cancellationToken));
        Assert.Equal(0, (await ReadExactlyAsync(stream, 20, cancellationToken))[0]);
        var initialEncodingHeader = await ReadExactlyAsync(stream, 4, cancellationToken);
        Assert.Equal(2, initialEncodingHeader[0]);
        var initialEncodingCount = BinaryPrimitives.ReadUInt16BigEndian(initialEncodingHeader.AsSpan(2));
        var initialEncodings = await ReadExactlyAsync(
            stream,
            initialEncodingCount * sizeof(int),
            cancellationToken);
        Assert.Contains(
            (int)RfbEncodingType.ArdSessionEncryption,
            Enumerable.Range(0, initialEncodingCount).Select(index =>
                BinaryPrimitives.ReadInt32BigEndian(initialEncodings.AsSpan(index * sizeof(int)))));
        Assert.Equal(
            new byte[] { 0x12, 0, 0, 1, 0, 1, 0, 1, 0, 0, 0, 1 },
            await ReadExactlyAsync(stream, 12, cancellationToken));

        var activationRequests = 0;
        var activationRequest = await ReadExactlyAsync(stream, 10, cancellationToken);
        activationRequests++;
        Assert.Equal(CreateFullRequest(4, 2), activationRequest);
        var sessionKey = Enumerable.Range(32, 16).Select(value => (byte)value).ToArray();
        var sessionIv = Enumerable.Range(64, 16).Select(value => (byte)value).ToArray();
        await stream.WriteAsync(
            CreateEncryptionActivationUpdate(authenticationKey, sessionKey, sessionIv),
            cancellationToken);
        Assert.Equal(
            new byte[] { 0x12, 0, 0, 2, 0, 1, 0, 0 },
            await ReadExactlyAsync(stream, 8, cancellationToken));

        var firstClientPacket = await ReadEncryptedPacketAsync(
            stream,
            sessionKey,
            sessionIv,
            sequence: 0,
            cancellationToken);
        var expectedSetEncodings = new byte[20];
        expectedSetEncodings[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(expectedSetEncodings.AsSpan(2), 4);
        BinaryPrimitives.WriteInt32BigEndian(expectedSetEncodings.AsSpan(4), candidateEncodingId);
        BinaryPrimitives.WriteInt32BigEndian(
            expectedSetEncodings.AsSpan(8),
            (int)RfbEncodingType.Zlib);
        BinaryPrimitives.WriteInt32BigEndian(
            expectedSetEncodings.AsSpan(12),
            (int)RfbEncodingType.Zrle);
        BinaryPrimitives.WriteInt32BigEndian(
            expectedSetEncodings.AsSpan(16),
            (int)RfbEncodingType.Raw);
        Assert.Equal(expectedSetEncodings, firstClientPacket.Payload);

        var update = CreateUpdate(candidateEncodingId, payloadPrefix, width: 4, height: 2);
        var requestPacket = await ReadEncryptedPacketAsync(
            stream,
            sessionKey,
            firstClientPacket.NextIv,
            sequence: 1,
            cancellationToken);
        Assert.Equal(CreateFullRequest(4, 2), requestPacket.Payload);
        var serverPacket = ArdEncryptedPacketCodec.Encrypt(
            sessionKey,
            sessionIv,
            sequence: 0,
            update);
        await stream.WriteAsync(serverPacket, cancellationToken);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], cancellationToken));
        Assert.InRange(activationRequests, 1, 8);
        return activationRequests;
    }

    private static byte[] CreateExtendedServerInit(ushort width, ushort height)
    {
        var extendedName = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(extendedName.AsSpan(2), (uint)ArdServerFlags.MayControl);
        Encoding.UTF8.GetBytes("Mac").CopyTo(extendedName, 23);
        var bytes = new byte[24 + extendedName.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), height);
        PixelFormat.WinArdBgra32.ToWireBytes().CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), checked((uint)extendedName.Length));
        extendedName.CopyTo(bytes, 24);
        return bytes;
    }

    private static byte[] CreateFullRequest(ushort width, ushort height)
    {
        var request = new byte[10];
        request[0] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), width);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), height);
        return request;
    }

    private static byte[] CreateEncryptionActivationUpdate(
        byte[] authenticationKey,
        byte[] sessionKey,
        byte[] sessionIv)
    {
        var update = new byte[4 + 12 + 36];
        BinaryPrimitives.WriteUInt16BigEndian(update.AsSpan(2), 1);
        BinaryPrimitives.WriteInt32BigEndian(
            update.AsSpan(12),
            (int)RfbEncodingType.ArdSessionEncryption);
        BinaryPrimitives.WriteUInt32BigEndian(update.AsSpan(16), 1);
#pragma warning disable CA5358 // AES-ECB is required to construct an ARD session fixture.
        using var aes = Aes.Create();
        aes.Key = authenticationKey;
        aes.EncryptEcb(sessionKey, PaddingMode.None).CopyTo(update, 20);
        aes.EncryptEcb(sessionIv, PaddingMode.None).CopyTo(update, 36);
#pragma warning restore CA5358
        return update;
    }

    private static async Task<(byte[] Payload, byte[] NextIv)> ReadEncryptedPacketAsync(
        Stream stream,
        byte[] key,
        byte[] iv,
        uint sequence,
        CancellationToken cancellationToken)
    {
        var header = await ReadExactlyAsync(stream, 2, cancellationToken);
        var ciphertext = await ReadExactlyAsync(
            stream,
            BinaryPrimitives.ReadUInt16BigEndian(header),
            cancellationToken);
        using var decoded = ArdEncryptedPacketCodec.Decrypt(key, iv, sequence, ciphertext);
        return (decoded.Payload.ToArray(), decoded.NextIv.ToArray());
    }

    private static async Task<byte[]> ReadExactlyAsync(
        Stream stream,
        int count,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[count];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("The loopback client closed before the scripted exchange completed.");
            }

            offset += read;
        }

        return bytes;
    }

    private static byte[] ToFixedWidth(BigInteger value, int width)
    {
        var source = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[width];
        source.CopyTo(result, width - source.Length);
        return result;
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

    private sealed class CancelDuringPayloadStream(
        byte[] bytes,
        CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length >= 64)
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled<int>(cancellationToken);
            }

            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
