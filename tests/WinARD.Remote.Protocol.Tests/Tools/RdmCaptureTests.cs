using System.Globalization;
using System.Text.Json;
using WinARD.ProtocolProbe.RdmCapture;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Tools;

public sealed class RdmCaptureTests
{
    [Fact]
    public async Task Capture_file_round_trips_camel_case_json_without_sensitive_fields()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "capture.json");
        var report = CreateReport(
            encodings: [0, -223, -309],
            messages:
            [
                new CapturedClientMessage(0, "SetPixelFormat", 20, "0020", null),
                new CapturedClientMessage(3, "FramebufferUpdateRequest", 10, "0000000004000300", null),
            ]);

        try
        {
            await RdmCaptureFile.WriteAsync(path, report, CancellationToken.None);

            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
            Assert.Contains("\"bitsPerPixel\": 32", json, StringComparison.Ordinal);
            Assert.DoesNotContain("SchemaVersion", json, StringComparison.Ordinal);
            foreach (var sensitiveName in new[] { "host", "username", "password", "credential", "rawAuthResponse" })
            {
                Assert.DoesNotContain(sensitiveName, json, StringComparison.OrdinalIgnoreCase);
            }

            var restored = await RdmCaptureFile.ReadAsync(path, CancellationToken.None);
            Assert.Equal(report.SchemaVersion, restored.SchemaVersion);
            Assert.Equal(report.Profile, restored.Profile);
            Assert.Equal(report.ClientVersion, restored.ClientVersion);
            Assert.Equal(report.ClientInit, restored.ClientInit);
            Assert.Equal(report.PixelFormat, restored.PixelFormat);
            Assert.Equal(report.Encodings, restored.Encodings);
            Assert.Equal(report.Messages, restored.Messages);
            Assert.Equal(report.ReachedFramebufferRequest, restored.ReachedFramebufferRequest);
            Assert.Equal(report.StoppedAtUnknownMessageType, restored.StoppedAtUnknownMessageType);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Write_creates_parent_directory_atomically_overwrites_and_removes_temporary_files()
    {
        var root = CreateTemporaryDirectory();
        var directory = Path.Combine(root, "nested", "captures");
        var path = Path.Combine(directory, "capture.json");
        try
        {
            await RdmCaptureFile.WriteAsync(path, CreateReport(profile: "first"), CancellationToken.None);
            await RdmCaptureFile.WriteAsync(path, CreateReport(profile: "second"), CancellationToken.None);

            var restored = await RdmCaptureFile.ReadAsync(path, CancellationToken.None);
            Assert.Equal("second", restored.Profile);
            Assert.Equal([Path.GetFullPath(path)], Directory.EnumerateFiles(directory).Select(Path.GetFullPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":2,\"profile\":\"valid\",\"clientVersion\":\"3.8\",\"clientInit\":1,\"encodings\":[],\"messages\":[],\"reachedFramebufferRequest\":false}")]
    [InlineData("{\"schemaVersion\":1,\"profile\":\"valid\",\"clientVersion\":null,\"clientInit\":1,\"encodings\":[],\"messages\":[],\"reachedFramebufferRequest\":false}")]
    [InlineData("{\"schemaVersion\":1,\"profile\":\"valid\",\"clientVersion\":\"3.8\",\"clientInit\":1,\"encodings\":null,\"messages\":[],\"reachedFramebufferRequest\":false}")]
    [InlineData("{\"schemaVersion\":1,\"profile\":\"valid\",\"clientVersion\":\"3.8\",\"clientInit\":1,\"encodings\":[],\"messages\":null,\"reachedFramebufferRequest\":false}")]
    [InlineData("{\"schemaVersion\":1,\"profile\":\"valid\",\"clientVersion\":\"3.8\",\"clientInit\":1,\"encodings\":[],\"messages\":[{\"type\":0,\"name\":null,\"wireLength\":1}],\"reachedFramebufferRequest\":false}")]
    public async Task Read_rejects_empty_unsupported_or_incomplete_models(string json)
    {
        await AssertInvalidCaptureAsync(json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("contains space")]
    [InlineData("contains_underscore")]
    [InlineData("123456789012345678901234567890123")]
    public async Task Read_rejects_invalid_profiles(string profile)
    {
        await AssertInvalidCaptureAsync(CreateJson(profile: profile));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("aa")]
    [InlineData("0G")]
    [InlineData("00-")]
    public async Task Read_rejects_invalid_numeric_payload_hex(string numericPayloadHex)
    {
        var messages = $"[{{\"type\":3,\"name\":\"Request\",\"wireLength\":10,\"numericPayloadHex\":{JsonSerializer.Serialize(numericPayloadHex)}}}]";

        await AssertInvalidCaptureAsync(CreateJson(messages: messages));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Read_rejects_oversized_lists(bool oversizeEncodings)
    {
        var entries = string.Join(',', Enumerable.Repeat("0", 4097));
        var encodings = oversizeEncodings ? $"[{entries}]" : "[]";
        var messages = oversizeEncodings
            ? "[]"
            : $"[{string.Join(',', Enumerable.Repeat("{\"type\":0,\"name\":\"Message\",\"wireLength\":1}", 4097))}]";

        await AssertInvalidCaptureAsync(CreateJson(encodings: encodings, messages: messages));
    }

    [Fact]
    public async Task Read_rejects_files_larger_than_four_mibibytes_before_deserialization()
    {
        const int maximumFileLength = 4 * 1024 * 1024;
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "oversized.json");
        try
        {
            await File.WriteAllTextAsync(path, new string(' ', maximumFileLength + 1));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                RdmCaptureFile.ReadAsync(path, CancellationToken.None));

            Assert.Contains(
                maximumFileLength.ToString(CultureInfo.InvariantCulture),
                exception.Message,
                StringComparison.Ordinal);
            Assert.Null(exception.InnerException);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Read_rejects_oversized_client_version()
    {
        await AssertInvalidCaptureAsync(CreateJson(clientVersion: new string('v', 33)));
    }

    [Fact]
    public async Task Read_rejects_oversized_message_name()
    {
        var messages = $"[{{\"type\":0,\"name\":{JsonSerializer.Serialize(new string('n', 65))},\"wireLength\":1}}]";

        await AssertInvalidCaptureAsync(CreateJson(messages: messages));
    }

    [Fact]
    public async Task Read_rejects_numeric_payload_hex_larger_than_4096_characters()
    {
        var messages = $"[{{\"type\":0,\"name\":\"Message\",\"wireLength\":1,\"numericPayloadHex\":{JsonSerializer.Serialize(new string('A', 4098))}}}]";

        await AssertInvalidCaptureAsync(CreateJson(messages: messages));
    }

    [Fact]
    public async Task Read_rejects_non_null_sha256_that_is_not_64_uppercase_hex_characters()
    {
        var invalidHashes = new[]
        {
            string.Empty,
            new string('A', 63),
            new string('a', 64),
            new string('G', 64),
        };

        foreach (var hash in invalidHashes)
        {
            var messages = $"[{{\"type\":0,\"name\":\"Message\",\"wireLength\":1,\"payloadSha256\":{JsonSerializer.Serialize(hash)}}}]";
            await AssertInvalidCaptureAsync(CreateJson(messages: messages));
        }
    }

    [Fact]
    public void Capture_report_snapshots_source_collections()
    {
        var encodings = new List<int> { 0 };
        var messages = new List<CapturedClientMessage>
        {
            new(3, "FramebufferUpdateRequest", 10, "0000000004000300", null),
        };
        var report = CreateReport(encodings: encodings, messages: messages);

        encodings.Add(-309);
        messages.Add(new CapturedClientMessage(0, "SetPixelFormat", 20, "0020", null));

        Assert.Equal([0], report.Encodings);
        Assert.Single(report.Messages);
        Assert.IsNotType<List<int>>(report.Encodings);
        Assert.IsNotType<List<CapturedClientMessage>>(report.Messages);
    }

    [Fact]
    public async Task Write_serializes_the_capture_snapshot_not_mutated_source_collections()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "snapshot.json");
        var encodings = new List<int> { 0 };
        var messages = new List<CapturedClientMessage>
        {
            new(3, "FramebufferUpdateRequest", 10, "0000000004000300", null),
        };
        var report = CreateReport(encodings: encodings, messages: messages);
        encodings.Add(-309);
        messages.Clear();

        try
        {
            await RdmCaptureFile.WriteAsync(path, report, CancellationToken.None);

            var restored = await RdmCaptureFile.ReadAsync(path, CancellationToken.None);
            Assert.Equal([0], restored.Encodings);
            Assert.Single(restored.Messages);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Write_uses_short_random_temporary_name_and_removes_it()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "capture-with-a-descriptive-target-name.json");
        var temporaryName = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, "*.tmp")
        {
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, args) => temporaryName.TrySetResult(args.Name ?? string.Empty);

        try
        {
            await RdmCaptureFile.WriteAsync(path, CreateReport(), CancellationToken.None);

            var observedName = await temporaryName.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Matches("^\\.winard-rdm-[0-9a-f]{32}\\.tmp$", observedName);
            Assert.DoesNotContain(Path.GetFileName(path), observedName, StringComparison.Ordinal);
            Assert.DoesNotContain(Directory.EnumerateFiles(directory), candidate =>
                string.Equals(Path.GetExtension(candidate), ".tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Comparer_returns_the_only_distinct_signed_adaptive_encoding_in_adaptive_order()
    {
        var baseline = CreateReport(encodings: [0, -223, 16]);
        var adaptive = CreateReport(encodings: [-223, -309, -309, 16]);

        var result = RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(baseline, adaptive);

        Assert.Equal(-309, result);
    }

    [Fact]
    public void Comparer_rejects_zero_adaptive_only_encodings()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(
                CreateReport(encodings: [0, -223]),
                CreateReport(encodings: [-223, 0, 0])));

        Assert.DoesNotContain("MVS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comparer_rejects_multiple_adaptive_only_encodings()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(
                CreateReport(encodings: [0]),
                CreateReport(encodings: [-309, -310])));

        Assert.Contains("2", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MVS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comparer_rejects_mismatched_schemas()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(
                CreateReport(schemaVersion: 1),
                CreateReport(schemaVersion: 2)));

        Assert.Contains("1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MVS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comparer_rejects_null_reports()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(null!, CreateReport()));
        Assert.Throws<ArgumentNullException>(() =>
            RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(CreateReport(), null!));
    }

    [Fact]
    public async Task Read_and_write_honor_pre_cancelled_tokens()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "capture.json");
        await File.WriteAllTextAsync(path, CreateJson());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RdmCaptureFile.ReadAsync(path, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RdmCaptureFile.WriteAsync(path, CreateReport(), cancellation.Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RdmCaptureReport CreateReport(
        int schemaVersion = 1,
        string profile = "adaptive-default",
        string clientVersion = "RFB 003.008",
        IReadOnlyList<int>? encodings = null,
        IReadOnlyList<CapturedClientMessage>? messages = null) =>
        new(
            schemaVersion,
            profile,
            clientVersion,
            1,
            new CapturedPixelFormat(32, 24, false, true, 255, 255, 255, 16, 8, 0),
            encodings ?? [0],
            messages ?? [new CapturedClientMessage(3, "FramebufferUpdateRequest", 10, "0000000004000300", null)],
            true,
            null);

    private static async Task AssertInvalidCaptureAsync(string json)
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "invalid.json");
        try
        {
            await File.WriteAllTextAsync(path, json);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                RdmCaptureFile.ReadAsync(path, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateJson(
        string profile = "valid-profile",
        string clientVersion = "RFB 003.008",
        string encodings = "[]",
        string messages = "[]") =>
        $$"""
        {
          "schemaVersion": 1,
          "profile": {{JsonSerializer.Serialize(profile)}},
          "clientVersion": {{JsonSerializer.Serialize(clientVersion)}},
          "clientInit": 1,
          "pixelFormat": null,
          "encodings": {{encodings}},
          "messages": {{messages}},
          "reachedFramebufferRequest": false,
          "stoppedAtUnknownMessageType": null
        }
        """;

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-rdm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
