using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using WinARD.ProtocolProbe;
using WinARD.ProtocolProbe.EncodingResearch;
using WinARD.ProtocolProbe.RdmCapture;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Testing.Rfb;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Tools;

public sealed class ProtocolProbeTests
{
    [Fact]
    public void Probe_command_line_defaults_to_authentication()
    {
        Assert.True(ProbeCommandLine.TryParse([], out var request));

        Assert.Equal(new ProbeRequest(ProbeMode.Authentication), request);
    }

    [Fact]
    public void Probe_command_line_parses_pointer_smoke()
    {
        Assert.True(ProbeCommandLine.TryParse(["--pointer-smoke"], out var request));

        Assert.Equal(new ProbeRequest(ProbeMode.PointerSmoke), request);
        Assert.Null(request.CaptureFirstFramePath);
    }

    [Fact]
    public void Probe_command_line_parses_capture_path_without_changing_it()
    {
        const string path = "captures/first frame.bmp";

        Assert.True(ProbeCommandLine.TryParse(["--capture-first-frame", path], out var request));

        Assert.Equal(new ProbeRequest(ProbeMode.CaptureFirstFrame, path), request);
    }

    [Fact]
    public void Probe_command_line_parses_rdm_listener()
    {
        Assert.True(ProbeCommandLine.TryParse(
            ["--listen-rdm", "adaptive-default", "capture.json"],
            out var request));

        Assert.Equal(ProbeMode.ListenRdm, request.Mode);
        Assert.Equal("adaptive-default", request.ProfileName);
        Assert.Equal("capture.json", request.OutputPath);
        Assert.Null(request.BaselineCapturePath);
        Assert.Null(request.AdaptiveCapturePath);
        Assert.False(request.SyntheticScreenConfirmed);
    }

    [Fact]
    public void Probe_command_line_parses_rdm_capture_comparison()
    {
        Assert.True(ProbeCommandLine.TryParse(
            ["--compare-rdm-captures", "full.json", "adaptive.json"],
            out var request));

        Assert.Equal(ProbeMode.CompareRdmCaptures, request.Mode);
        Assert.Equal("full.json", request.BaselineCapturePath);
        Assert.Equal("adaptive.json", request.AdaptiveCapturePath);
        Assert.Null(request.OutputPath);
        Assert.Null(request.ProfileName);
    }

    [Fact]
    public void Probe_command_line_parses_confirmed_known_prefix_capture()
    {
        Assert.True(ProbeCommandLine.TryParse(
            [
                "--capture-known-encoding-prefix",
                "1002",
                "capture-directory",
                "--confirm-synthetic-screen",
            ],
            out var request));

        Assert.Equal(ProbeMode.CaptureKnownEncodingPrefix, request.Mode);
        Assert.Equal(1002, request.CandidateEncodingId);
        Assert.Equal("capture-directory", request.OutputPath);
        Assert.True(request.SyntheticScreenConfirmed);
    }

    [Fact]
    public void Probe_command_line_requires_synthetic_screen_confirmation()
    {
        Assert.False(ProbeCommandLine.TryParse(
            ["--capture-known-encoding-prefix", "1001", "capture-directory"],
            out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("contains space")]
    [InlineData("contains_underscore")]
    [InlineData("123456789012345678901234567890123")]
    public void Probe_command_line_rejects_invalid_rdm_profile(string profile)
    {
        Assert.False(ProbeCommandLine.TryParse(
            ["--listen-rdm", profile, "capture.json"],
            out _));
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--pointer-smoke", "extra")]
    [InlineData("--capture-first-frame")]
    [InlineData("--capture-first-frame", "frame.bgra", "extra")]
    [InlineData("--pointer-smoke", "--capture-first-frame", "frame.bgra")]
    [InlineData("--capture-first-frame", "frame.bgra", "--pointer-smoke")]
    [InlineData("--listen-rdm", "adaptive-default")]
    [InlineData("--listen-rdm", "adaptive-default", "capture.json", "extra")]
    [InlineData("--compare-rdm-captures", "full.json")]
    [InlineData("--compare-rdm-captures", "full.json", "adaptive.json", "extra")]
    [InlineData("--capture-known-encoding-prefix", "1000", "out", "--confirm-synthetic-screen")]
    [InlineData("--capture-known-encoding-prefix", "1002", "", "--confirm-synthetic-screen")]
    [InlineData("--capture-known-encoding-prefix", "1002", "out", "--wrong")]
    [InlineData("--capture-known-encoding-prefix", "1002", "out", "--confirm-synthetic-screen", "--confirm-synthetic-screen")]
    public void Probe_command_line_rejects_invalid_arguments(params string[] args)
    {
        Assert.False(ProbeCommandLine.TryParse(args, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Probe_command_line_rejects_blank_capture_path(string path)
    {
        Assert.False(ProbeCommandLine.TryParse(["--capture-first-frame", path], out _));
    }

    [Fact]
    public void Probe_command_line_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => ProbeCommandLine.TryParse(null!, out _));
    }

    [Fact]
    public void Rdm_output_formatters_emit_only_safe_expected_fields()
    {
        Assert.Equal(
            "RDM capture listening on 127.0.0.1:5901 for profile adaptive-default.",
            ProbeOutput.FormatRdmListening(5901, "adaptive-default"));
        Assert.Equal(
            @"RDM capture saved: artifacts\protocol-research\rdm\adaptive-default.json",
            ProbeOutput.FormatRdmSaved(@"artifacts\protocol-research\rdm\adaptive-default.json"));

        var comparison = ProbeOutput.FormatRdmComparisonCandidate(-309);
        Assert.Equal("RDM comparison candidate signed encoding ID: -309.", comparison);
        Assert.DoesNotContain("MVS", comparison, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", comparison, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-workstation", comparison, StringComparison.Ordinal);
    }

    [Fact]
    public void Encoding_prefix_output_formatter_emits_only_safe_metadata()
    {
        var capture = new EncodingPrefixCapture(
            1,
            12345,
            new CapturedRectangle(0, 0, 1920, 1080),
            4,
            new string('A', 64),
            [0xDE, 0xAD, 0xBE, 0xEF]);

        var formatted = ProbeOutput.FormatEncodingPrefixCaptured(capture);

        Assert.Equal(
            "Encoding prefix captured: signed encoding ID 12345, rectangle (0,0) 1920x1080, 4 bytes. Saved as manifest.json and payload-prefix.bin.",
            formatted);
        Assert.DoesNotContain("DEADBEEF", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(capture.PayloadSha256, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("MVS", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Known_prefix_command_dispatches_capture_without_printing_sensitive_values()
    {
        const string host = "private-workstation.invalid";
        const string usernameText = "private-user";
        const string passwordText = "private-password";
        var calls = 0;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await global::Program.RunAsync(
            [
                "--capture-known-encoding-prefix",
                "1001",
                "prefix-output",
                "--confirm-synthetic-screen",
            ],
            name => name switch
            {
                "WINARD_HOST" => host,
                "WINARD_USERNAME" => usernameText,
                "WINARD_PORT" => "5900",
                _ => null,
            },
            output,
            error,
            CancellationToken.None,
            captureEncodingPrefix: (capturedHost, port, username, password, candidate, directory, confirmed, _) =>
            {
                calls++;
                Assert.Equal(host, capturedHost);
                Assert.Equal(5900, port);
                Assert.Equal(1001, candidate);
                Assert.Equal("prefix-output", directory);
                Assert.True(confirmed);
                Assert.Equal(usernameText.Length, username.Length);
                Assert.Equal(passwordText.Length, password.Length);
                return Task.FromResult<IReadOnlyList<EncodingPrefixCapture>>([new EncodingPrefixCapture(
                    1,
                    12345,
                    new CapturedRectangle(0, 0, 1920, 1080),
                    4,
                    new string('A', 64),
                    [1, 2, 3, 4])]);
            },
            readPassword: _ => SecretMaterial.FromUtf8(passwordText));

        var console = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Equal(1, calls);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Encoding prefix captured", console, StringComparison.Ordinal);
        Assert.DoesNotContain(host, console, StringComparison.Ordinal);
        Assert.DoesNotContain(usernameText, console, StringComparison.Ordinal);
        Assert.DoesNotContain(passwordText, console, StringComparison.Ordinal);
        Assert.DoesNotContain("01020304", console, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_known_prefix_confirmation_fails_before_environment_or_capture()
    {
        var environmentRead = false;
        var captureStarted = false;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await global::Program.RunAsync(
            ["--capture-known-encoding-prefix", "1002", "prefix-output"],
            _ => { environmentRead = true; return null; },
            output,
            error,
            CancellationToken.None,
            captureEncodingPrefix: (_, _, _, _, _, _, _, _) =>
            {
                captureStarted = true;
                throw new InvalidOperationException();
            });

        Assert.Equal(2, exitCode);
        Assert.False(environmentRead);
        Assert.False(captureStarted);
    }

    [Theory]
    [InlineData(null, true, 5901)]
    [InlineData("", true, 5901)]
    [InlineData("5902", true, 5902)]
    [InlineData("not-a-port", false, 0)]
    [InlineData("0", false, 0)]
    [InlineData("65536", false, 0)]
    public void Rdm_listener_port_uses_safe_default_and_rejects_invalid_values(
        string? value,
        bool expectedSuccess,
        int expectedPort)
    {
        var success = global::Program.TryParseRdmListenPort(value, out var port);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData(null, true, 10)]
    [InlineData("", true, 10)]
    [InlineData("1", true, 1)]
    [InlineData("120", true, 120)]
    [InlineData("600", true, 600)]
    [InlineData("not-a-timeout", false, 0)]
    [InlineData("0", false, 0)]
    [InlineData("601", false, 0)]
    public void Rdm_listener_idle_timeout_uses_safe_default_and_rejects_invalid_values(
        string? value,
        bool expectedSuccess,
        int expectedSeconds)
    {
        var success = global::Program.TryParseRdmIdleTimeout(value, out var timeout);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), timeout);
    }

    [Fact]
    public void Usage_covers_all_research_commands_with_powershell_call_operator()
    {
        var usage = global::Program.UsageText;

        Assert.Contains(
            @"& '.\WinARD.ProtocolProbe.exe' --listen-rdm adaptive-default '.\artifacts\protocol-research\rdm\adaptive-default.json'",
            usage,
            StringComparison.Ordinal);
        Assert.Contains("--compare-rdm-captures", usage, StringComparison.Ordinal);
        Assert.Contains("--capture-known-encoding-prefix", usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_rdm_listener_port_returns_usage_error_before_remote_credentials_are_read()
    {
        var requestedVariables = new List<string>();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await global::Program.RunAsync(
            ["--listen-rdm", "adaptive-default", "capture.json"],
            name =>
            {
                requestedVariables.Add(name);
                return name == "WINARD_LISTEN_PORT"
                    ? "invalid"
                    : throw new InvalidOperationException($"Remote variable {name} must not be read.");
            },
            output,
            error,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(["WINARD_LISTEN_PORT"], requestedVariables);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("--listen-rdm", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_rdm_listener_idle_timeout_returns_usage_error_without_starting_listener()
    {
        var requestedVariables = new List<string>();
        var captureStarted = false;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await global::Program.RunAsync(
            ["--listen-rdm", "adaptive-default", "capture.json"],
            name =>
            {
                requestedVariables.Add(name);
                return name switch
                {
                    "WINARD_LISTEN_PORT" => "5902",
                    "WINARD_RDM_IDLE_TIMEOUT_SECONDS" => "invalid",
                    _ => throw new InvalidOperationException($"Remote variable {name} must not be read."),
                };
            },
            output,
            error,
            CancellationToken.None,
            (_, _, _, _) =>
            {
                captureStarted = true;
                throw new InvalidOperationException("Listener must not start.");
            });

        Assert.Equal(2, exitCode);
        Assert.Equal(
            ["WINARD_LISTEN_PORT", "WINARD_RDM_IDLE_TIMEOUT_SECONDS"],
            requestedVariables);
        Assert.False(captureStarted);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("--listen-rdm", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rdm_listener_dispatches_capture_and_file_write_before_remote_credentials_are_read()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-probe-rdm-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "adaptive-default.json");
        var requestedVariables = new List<string>();
        var capturedArguments = new List<(TimeSpan Timeout, int Port, string Profile)>();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            var exitCode = await global::Program.RunAsync(
                ["--listen-rdm", "adaptive-default", path],
                name =>
                {
                    requestedVariables.Add(name);
                    return name switch
                    {
                        "WINARD_LISTEN_PORT" => "5902",
                        "WINARD_RDM_IDLE_TIMEOUT_SECONDS" => "120",
                        _ => throw new InvalidOperationException($"Remote variable {name} must not be read."),
                    };
                },
                output,
                error,
                CancellationToken.None,
                (timeout, port, profile, _) =>
                {
                    capturedArguments.Add((timeout, port, profile));
                    return Task.FromResult(CreateRdmReport("adaptive-default", [0, -223, -309]));
                });

            Assert.Equal(0, exitCode);
            Assert.Equal(
                ["WINARD_LISTEN_PORT", "WINARD_RDM_IDLE_TIMEOUT_SECONDS"],
                requestedVariables);
            Assert.Equal([(TimeSpan.FromSeconds(120), 5902, "adaptive-default")], capturedArguments);
            Assert.Equal(
                ProbeOutput.FormatRdmListening(5902, "adaptive-default")
                    + Environment.NewLine
                    + ProbeOutput.FormatRdmSaved(path)
                    + Environment.NewLine,
                output.ToString());
            Assert.Equal(string.Empty, error.ToString());
            var restored = await RdmCaptureFile.ReadAsync(path, CancellationToken.None);
            Assert.Equal("adaptive-default", restored.Profile);
            Assert.Equal([0, -223, -309], restored.Encodings);
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
    public async Task Rdm_comparison_reads_files_and_outputs_only_candidate_before_remote_credentials_are_read()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"winard-probe-rdm-{Guid.NewGuid():N}");
        var baselinePath = Path.Combine(directory, "baseline.json");
        var adaptivePath = Path.Combine(directory, "adaptive.json");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            await RdmCaptureFile.WriteAsync(
                baselinePath,
                CreateRdmReport("full", [0, -223]),
                CancellationToken.None);
            await RdmCaptureFile.WriteAsync(
                adaptivePath,
                CreateRdmReport("adaptive-default", [0, -223, -309]),
                CancellationToken.None);

            var exitCode = await global::Program.RunAsync(
                ["--compare-rdm-captures", baselinePath, adaptivePath],
                name => throw new InvalidOperationException($"Environment variable {name} must not be read."),
                output,
                error,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(
                ProbeOutput.FormatRdmComparisonCandidate(-309) + Environment.NewLine,
                output.ToString());
            Assert.Equal(string.Empty, error.ToString());
            Assert.DoesNotContain("MVS", output.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(baselinePath, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(adaptivePath, output.ToString(), StringComparison.Ordinal);
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
    public async Task Default_probe_exits_after_authentication_without_initializing_framebuffer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var stageReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = RunScriptedServerAsync(listener, null, null, stageReached);
        using var username = SecretMaterial.FromUtf8("probe-user");
        using var password = SecretMaterial.FromUtf8("probe-password");

        var result = await new ProbeRunner(TimeSpan.FromSeconds(5)).RunAsync(
            IPAddress.Loopback.ToString(),
            GetPort(listener),
            username,
            password,
            CancellationToken.None);

        Assert.Equal(RfbVersion.V3_8, result.Version);
        Assert.Equal(RfbSecurityType.AppleRemoteDesktop, result.SecurityType);
        Assert.Null(result.Capture);
        await stageReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Legacy_capture_overload_accepts_null_literal_without_ambiguity()
    {
        using var username = SecretMaterial.FromUtf8("legacy-user");
        using var password = SecretMaterial.FromUtf8("legacy-password");
        var runner = new ProbeRunner(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(
                " ",
                5900,
                username,
                password,
                null,
                CancellationToken.None));
    }

    [Fact]
    public void Probe_result_preserves_three_item_deconstruction()
    {
        var result = new ProbeResult(RfbVersion.V3_8, RfbSecurityType.AppleRemoteDesktop, null);

        var (version, securityType, capture) = result;

        Assert.Equal(RfbVersion.V3_8, version);
        Assert.Equal(RfbSecurityType.AppleRemoteDesktop, securityType);
        Assert.Null(capture);
    }

    [Theory]
    [InlineData(ProbeMode.Authentication, "unexpected.bgra")]
    [InlineData(ProbeMode.PointerSmoke, "unexpected.bgra")]
    [InlineData(ProbeMode.CaptureFirstFrame, null)]
    [InlineData(ProbeMode.CaptureFirstFrame, "")]
    [InlineData(ProbeMode.CaptureFirstFrame, " ")]
    public async Task Invalid_probe_request_shape_is_rejected_before_connect(
        ProbeMode mode,
        string? captureFirstFramePath)
    {
        using var username = SecretMaterial.FromUtf8("validation-user");
        using var password = SecretMaterial.FromUtf8("validation-password");
        var runner = new ProbeRunner(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunRequestAsync(
                "must-not-resolve.invalid",
                5900,
                username,
                password,
                new ProbeRequest(mode, captureFirstFramePath),
                CancellationToken.None));

        Assert.Equal("request", exception.ParamName);
    }

    [Fact]
    public async Task Undefined_probe_mode_is_rejected_before_connect()
    {
        using var username = SecretMaterial.FromUtf8("validation-user");
        using var password = SecretMaterial.FromUtf8("validation-password");
        var runner = new ProbeRunner(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunRequestAsync(
                "must-not-resolve.invalid",
                5900,
                username,
                password,
                new ProbeRequest((ProbeMode)int.MaxValue),
                CancellationToken.None));

        Assert.Equal("request", exception.ParamName);
    }

    [Fact]
    public async Task Pointer_smoke_probe_completes_full_889_control_script_before_pointer_action()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunFullArd889PointerSmokeServerAsync(listener);
        using var username = SecretMaterial.FromUtf8("pointer-user");
        using var password = SecretMaterial.FromUtf8("pointer-password");

        var result = await new ProbeRunner(TimeSpan.FromSeconds(5)).RunRequestAsync(
            IPAddress.Loopback.ToString(),
            GetPort(listener),
            username,
            password,
            new ProbeRequest(ProbeMode.PointerSmoke),
            CancellationToken.None);

        Assert.Null(result.Capture);
        Assert.Equal(RfbVersion.V3_889, result.Version);
        Assert.Equal(RfbSecurityType.AppleRemoteDesktop, result.SecurityType);
        Assert.Equal(new ProbePointerSmoke(4, 2, 2, 1), result.PointerSmoke);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Pointer_smoke_output_reports_move_without_claiming_server_execution_or_secrets()
    {
        const string host = "private-host";
        const string username = "private-user";
        const string password = "private-password";

        var output = ProbeOutput.FormatPointerSmoke(new ProbePointerSmoke(4, 2, 2, 1));

        Assert.Contains("(2,1)", output, StringComparison.Ordinal);
        Assert.Contains("4x2", output, StringComparison.Ordinal);
        Assert.Contains("does not confirm", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(host, output, StringComparison.Ordinal);
        Assert.DoesNotContain(username, output, StringComparison.Ordinal);
        Assert.DoesNotContain(password, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_probe_initializes_requests_full_frame_and_writes_requested_format()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunCaptureServerAsync(listener);
        using var username = SecretMaterial.FromUtf8("capture-user");
        using var password = SecretMaterial.FromUtf8("capture-password");
        var path = Path.Combine(Path.GetTempPath(), $"winard-capture-{Guid.NewGuid():N}.bgra");
        try
        {
            var result = await new ProbeRunner(TimeSpan.FromSeconds(5)).RunAsync(
                IPAddress.Loopback.ToString(),
                GetPort(listener),
                username,
                password,
                path,
                CancellationToken.None);

            var capture = Assert.IsType<ProbeCapture>(result.Capture);
            Assert.Equal(1, capture.Width);
            Assert.Equal(1, capture.Height);
            Assert.Equal(Path.GetFullPath(path), capture.Path);
            Assert.Equal(new FramebufferRect(0, 0, 1, 1), Assert.Single(capture.DirtyRects));
            Assert.Contains("Dirty: (0,0) 1x1", ProbeOutput.FormatCapture(capture), StringComparison.Ordinal);
            Assert.Equal([0, 0, 255, 255], await File.ReadAllBytesAsync(path));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Capture_probe_skips_889_ack_and_nop_before_framebuffer_update()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunCaptureServerAsync(listener, CaptureScenario.FullRaw, ard889: true, [0x04, 0x07]);
        using var username = SecretMaterial.FromUtf8("capture-user");
        using var password = SecretMaterial.FromUtf8("capture-password");
        var path = Path.Combine(Path.GetTempPath(), $"winard-capture-{Guid.NewGuid():N}.bgra");
        try
        {
            var result = await new ProbeRunner(TimeSpan.FromSeconds(5)).RunAsync(
                IPAddress.Loopback.ToString(), GetPort(listener), username, password, path, CancellationToken.None);

            Assert.Equal(RfbVersion.V3_889, result.Version);
            Assert.Equal([0, 0, 255, 255], await File.ReadAllBytesAsync(path));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData((byte)0x04)]
    [InlineData((byte)0x07)]
    public async Task Capture_probe_rejects_ard_control_messages_for_standard_rfb(byte messageType)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunCaptureServerAsync(listener, CaptureScenario.FullRaw, messagesBeforeUpdate: [messageType]);
        using var username = SecretMaterial.FromUtf8("capture-user");
        using var password = SecretMaterial.FromUtf8("capture-password");
        var path = Path.Combine(Path.GetTempPath(), $"winard-capture-{Guid.NewGuid():N}.bgra");
        try
        {
            var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
                new ProbeRunner(TimeSpan.FromSeconds(5)).RunAsync(
                    IPAddress.Loopback.ToString(), GetPort(listener), username, password, path, CancellationToken.None));

            Assert.Contains(messageType.ToString(CultureInfo.InvariantCulture), exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Capture_probe_rejects_unknown_889_message_without_guessing_payload()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunCaptureServerAsync(listener, CaptureScenario.FullRaw, ard889: true, [0x08]);
        using var username = SecretMaterial.FromUtf8("capture-user");
        using var password = SecretMaterial.FromUtf8("capture-password");
        var path = Path.Combine(Path.GetTempPath(), $"winard-capture-{Guid.NewGuid():N}.bgra");
        try
        {
            var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
                new ProbeRunner(TimeSpan.FromSeconds(5)).RunAsync(
                    IPAddress.Loopback.ToString(), GetPort(listener), username, password, path, CancellationToken.None));

            Assert.Contains("8", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Capture_probe_rejects_update_without_dirty_rectangles()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunCaptureServerAsync(listener, sendEmptyUpdate: true);
        using var username = SecretMaterial.FromUtf8("capture-user");
        using var password = SecretMaterial.FromUtf8("capture-password");
        var path = Path.Combine(Path.GetTempPath(), $"winard-capture-{Guid.NewGuid():N}.bgra");
        try
        {
            var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
                new ProbeRunner(TimeSpan.FromSeconds(5)).RunAsync(
                    IPAddress.Loopback.ToString(),
                    GetPort(listener),
                    username,
                    password,
                    path,
                    CancellationToken.None));

            Assert.NotEmpty(exception.Message);
            Assert.False(File.Exists(path));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Capture_probe_rejects_cursor_only_update_as_first_frame()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunCaptureServerAsync(listener, sendCursorOnly: true);
        using var username = SecretMaterial.FromUtf8("capture-user");
        using var password = SecretMaterial.FromUtf8("capture-password");
        var path = Path.Combine(Path.GetTempPath(), $"winard-capture-{Guid.NewGuid():N}.bgra");
        try
        {
            await Assert.ThrowsAsync<RfbProtocolException>(() =>
                new ProbeRunner(TimeSpan.FromSeconds(5)).RunAsync(
                    IPAddress.Loopback.ToString(),
                    GetPort(listener),
                    username,
                    password,
                    path,
                    CancellationToken.None));

            Assert.False(File.Exists(path));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Capture_probe_reissues_full_request_after_desktop_resize_without_publishing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunCaptureServerAsync(listener, CaptureScenario.DesktopSizeOnly);
        using var username = SecretMaterial.FromUtf8("capture-user");
        using var password = SecretMaterial.FromUtf8("capture-password");
        var path = Path.Combine(Path.GetTempPath(), $"winard-capture-{Guid.NewGuid():N}.bgra");
        try
        {
            await Assert.ThrowsAsync<RfbProtocolException>(() =>
                new ProbeRunner(TimeSpan.FromSeconds(5)).RunAsync(
                    IPAddress.Loopback.ToString(),
                    GetPort(listener),
                    username,
                    password,
                    path,
                    CancellationToken.None));

            Assert.False(File.Exists(path));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Capture_probe_uses_resized_dimensions_for_reissued_full_request()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunCaptureServerAsync(listener, CaptureScenario.DesktopSizeThenFullRaw);
        using var username = SecretMaterial.FromUtf8("capture-user");
        using var password = SecretMaterial.FromUtf8("capture-password");
        var path = Path.Combine(Path.GetTempPath(), $"winard-capture-{Guid.NewGuid():N}.bgra");
        try
        {
            var result = await new ProbeRunner(TimeSpan.FromSeconds(5)).RunAsync(
                IPAddress.Loopback.ToString(),
                GetPort(listener),
                username,
                password,
                path,
                CancellationToken.None);

            var capture = Assert.IsType<ProbeCapture>(result.Capture);
            Assert.Equal(2, capture.Width);
            Assert.Equal(1, capture.Height);
            Assert.Equal([0, 0, 255, 255, 0, 255, 0, 255], await File.ReadAllBytesAsync(path));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Capture_probe_accumulates_partial_raw_coverage_across_updates()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunCaptureServerAsync(listener, CaptureScenario.PartialRawAcrossUpdates);
        using var username = SecretMaterial.FromUtf8("capture-user");
        using var password = SecretMaterial.FromUtf8("capture-password");
        var path = Path.Combine(Path.GetTempPath(), $"winard-capture-{Guid.NewGuid():N}.bgra");
        try
        {
            var result = await new ProbeRunner(TimeSpan.FromSeconds(5)).RunAsync(
                IPAddress.Loopback.ToString(),
                GetPort(listener),
                username,
                password,
                path,
                CancellationToken.None);

            var capture = Assert.IsType<ProbeCapture>(result.Capture);
            Assert.Equal(2, capture.Width);
            Assert.Equal(1, capture.Height);
            Assert.Equal([0, 0, 255, 255, 0, 255, 0, 255], await File.ReadAllBytesAsync(path));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Capture_probe_accepts_complete_frame_on_sixty_fourth_update()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunCaptureServerAsync(listener, CaptureScenario.CompleteOnSixtyFourthUpdate);
        using var username = SecretMaterial.FromUtf8("capture-user");
        using var password = SecretMaterial.FromUtf8("capture-password");
        var path = Path.Combine(Path.GetTempPath(), $"winard-capture-{Guid.NewGuid():N}.bgra");
        try
        {
            var result = await new ProbeRunner(TimeSpan.FromSeconds(5)).RunAsync(
                IPAddress.Loopback.ToString(),
                GetPort(listener),
                username,
                password,
                path,
                CancellationToken.None);

            Assert.NotNull(result.Capture);
            Assert.Equal([0, 0, 255, 255], await File.ReadAllBytesAsync(path));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Capture_probe_rejects_incomplete_sixty_fourth_update_without_requesting_sixty_fifth()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunCaptureServerAsync(listener, CaptureScenario.IncompleteAfterSixtyFourUpdates);
        using var username = SecretMaterial.FromUtf8("capture-user");
        using var password = SecretMaterial.FromUtf8("capture-password");
        var path = Path.Combine(Path.GetTempPath(), $"winard-capture-{Guid.NewGuid():N}.bgra");
        try
        {
            var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
                new ProbeRunner(TimeSpan.FromSeconds(5)).RunAsync(
                    IPAddress.Loopback.ToString(),
                    GetPort(listener),
                    username,
                    password,
                    path,
                    CancellationToken.None));

            Assert.Contains("64", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Capture_output_limits_dirty_rectangles_and_reports_omitted_count()
    {
        var capture = new ProbeCapture(
            "capture.bgra",
            1,
            1,
            Enumerable.Range(0, 10).Select(x => new FramebufferRect(x, 0, 1, 1)));

        var output = ProbeOutput.FormatCapture(capture);

        Assert.Contains("(7,0) 1x1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(8,0) 1x1", output, StringComparison.Ordinal);
        Assert.Contains("2 omitted", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_password_ctrl_c_throws_and_restores_console_state()
    {
        var console = new FakePasswordConsole(
            treatControlCAsInput: false,
            new ConsoleKeyInfo('\u0003', ConsoleKey.C, shift: false, alt: false, control: true));
        char[]? characterStorage = null;
        HiddenPasswordReader.CleanupObserver = (characters, _) => characterStorage = characters;

        try
        {
            Assert.Throws<OperationCanceledException>(() => HiddenPasswordReader.Read(console, CancellationToken.None));

            Assert.NotNull(characterStorage);
            Assert.All(characterStorage, character => Assert.Equal('\0', character));
            Assert.False(console.TreatControlCAsInput);
            Assert.Equal([true, false], console.TreatControlCAsInputAssignments);
        }
        finally
        {
            HiddenPasswordReader.CleanupObserver = null;
        }
    }

    [Fact]
    public void Hidden_password_reads_normally_and_restores_existing_console_state()
    {
        var console = new FakePasswordConsole(
            treatControlCAsInput: true,
            new ConsoleKeyInfo('p', ConsoleKey.P, shift: false, alt: false, control: false),
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        char[]? characterStorage = null;
        HiddenPasswordReader.CleanupObserver = (characters, _) => characterStorage = characters;

        try
        {
            using var password = HiddenPasswordReader.Read(console, CancellationToken.None);
            var copy = new byte[password!.Length];
            password.CopyTo(copy);

            Assert.Equal("p", Encoding.UTF8.GetString(copy));
            Assert.NotNull(characterStorage);
            Assert.All(characterStorage, character => Assert.Equal('\0', character));
            Assert.True(console.TreatControlCAsInput);
            Assert.Equal([true, true], console.TreatControlCAsInputAssignments);
        }
        finally
        {
            HiddenPasswordReader.CleanupObserver = null;
        }
    }

    [Fact]
    public void Hidden_password_zeroes_input_and_disposes_result_when_console_restore_throws()
    {
        var console = new FakePasswordConsole(
            treatControlCAsInput: false,
            new ConsoleKeyInfo('p', ConsoleKey.P, shift: false, alt: false, control: false),
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false))
        {
            TreatControlCAsInputExceptionAssignment = 2,
        };
        char[]? characterStorage = null;
        SecretMaterial? createdResult = null;
        HiddenPasswordReader.CleanupObserver = (characters, result) =>
        {
            characterStorage = characters;
            createdResult = result;
        };

        try
        {
            Assert.Throws<IOException>(() => HiddenPasswordReader.Read(console, CancellationToken.None));

            Assert.NotNull(characterStorage);
            Assert.All(characterStorage, character => Assert.Equal('\0', character));
            Assert.NotNull(createdResult);
            var field = typeof(SecretMaterial).GetField("_bytes", BindingFlags.Instance | BindingFlags.NonPublic);
            var secretStorage = Assert.IsType<byte[]>(field!.GetValue(createdResult));
            Assert.All(secretStorage, value => Assert.Equal(0, value));
            Assert.Throws<ObjectDisposedException>(() => _ = createdResult.Length);
            Assert.Equal([true, false], console.TreatControlCAsInputAssignments);
        }
        finally
        {
            HiddenPasswordReader.CleanupObserver = null;
        }
    }

    [Fact]
    public void Hidden_password_zeroes_input_and_restores_console_when_read_key_throws()
    {
        var console = new FakePasswordConsole(
            treatControlCAsInput: false,
            new ConsoleKeyInfo('p', ConsoleKey.P, shift: false, alt: false, control: false))
        {
            ReadKeyExceptionAfter = 1,
        };
        char[]? characterStorage = null;
        HiddenPasswordReader.CleanupObserver = (characters, _) => characterStorage = characters;

        try
        {
            Assert.Throws<IOException>(() => HiddenPasswordReader.Read(console, CancellationToken.None));

            Assert.NotNull(characterStorage);
            Assert.All(characterStorage, character => Assert.Equal('\0', character));
            Assert.False(console.TreatControlCAsInput);
            Assert.Equal([true, false], console.TreatControlCAsInputAssignments);
        }
        finally
        {
            HiddenPasswordReader.CleanupObserver = null;
        }
    }

    [Fact]
    public void Hidden_password_zeroes_input_and_restores_console_when_initial_state_change_throws()
    {
        var console = new FakePasswordConsole(treatControlCAsInput: false)
        {
            TreatControlCAsInputExceptionAssignment = 1,
        };
        char[]? characterStorage = null;
        HiddenPasswordReader.CleanupObserver = (characters, _) => characterStorage = characters;

        try
        {
            Assert.Throws<IOException>(() => HiddenPasswordReader.Read(console, CancellationToken.None));

            Assert.NotNull(characterStorage);
            Assert.All(characterStorage, character => Assert.Equal('\0', character));
            Assert.False(console.TreatControlCAsInput);
            Assert.Equal([true, false], console.TreatControlCAsInputAssignments);
        }
        finally
        {
            HiddenPasswordReader.CleanupObserver = null;
        }
    }

    [Fact]
    public async Task Overall_timeout_bounds_banner_challenge_and_result_silence_across_repeated_runs()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var silentStage = (SilentStage)(attempt % 3);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var stageReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serverTask = RunScriptedServerAsync(listener, silentStage, null, stageReached);
            using var username = SecretMaterial.FromUtf8("timeout-user");
            using var password = SecretMaterial.FromUtf8("timeout-password");
            var runner = new ProbeRunner(TimeSpan.FromMilliseconds(500));

            var exception = await Assert.ThrowsAsync<ProbeTimeoutException>(() =>
                runner.RunAsync(
                    IPAddress.Loopback.ToString(),
                    GetPort(listener),
                    username,
                    password,
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal("Probe timed out.", ProbeOutput.FormatFailure(exception));
            Assert.DoesNotContain('\n', ProbeOutput.FormatFailure(exception));
            await stageReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task User_cancellation_is_not_reported_as_operation_timeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var stageReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = RunScriptedServerAsync(listener, SilentStage.Banner, null, stageReached);
        using var username = SecretMaterial.FromUtf8("cancel-user");
        using var password = SecretMaterial.FromUtf8("cancel-password");
        using var cancellation = new CancellationTokenSource();
        var runner = new ProbeRunner(TimeSpan.FromSeconds(5));
        var runTask = runner.RunAsync(
            IPAddress.Loopback.ToString(),
            GetPort(listener),
            username,
            password,
            cancellation.Token);
        await stageReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsNotType<ProbeTimeoutException>(exception);
        Assert.Equal("Cancelled.", ProbeOutput.FormatFailure(exception));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Probe_failure_output_does_not_contain_echoed_username_or_password()
    {
        const string testUsername = "probe-echo-user";
        const string testPassword = "probe-echo-password";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var stageReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reason = Encoding.UTF8.GetBytes($"Denied {testUsername} / {testPassword} again {testPassword}");
        var serverTask = RunScriptedServerAsync(listener, null, reason, stageReached);
        using var username = SecretMaterial.FromUtf8(testUsername);
        using var password = SecretMaterial.FromUtf8(testPassword);
        var runner = new ProbeRunner(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<ArdAuthenticationRejectedException>(() =>
            runner.RunAsync(
                IPAddress.Loopback.ToString(),
                GetPort(listener),
                username,
                password,
                CancellationToken.None));
        var output = ProbeOutput.FormatFailure(exception);

        Assert.DoesNotContain(testUsername, output, StringComparison.Ordinal);
        Assert.DoesNotContain(testPassword, output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", output, StringComparison.Ordinal);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static RdmCaptureReport CreateRdmReport(string profile, IReadOnlyList<int> encodings) =>
        new(
            1,
            profile,
            "RFB 003.889",
            0xC1,
            new CapturedPixelFormat(32, 24, false, true, 255, 255, 255, 16, 8, 0),
            encodings,
            [new CapturedClientMessage(3, "FramebufferUpdateRequest", 10, "000000000007800438", null)],
            true,
            null);

    private static async Task RunScriptedServerAsync(
        TcpListener listener,
        SilentStage? silentStage,
        byte[]? rejectionReason,
        TaskCompletionSource stageReached)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        if (silentStage == SilentStage.Banner)
        {
            stageReached.TrySetResult();
            await WaitForClientCloseAsync(stream);
            return;
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        _ = await ReadExactlyAsync(stream, 12);
        await stream.WriteAsync(new byte[] { 1, (byte)RfbSecurityType.AppleRemoteDesktop });
        _ = await ReadExactlyAsync(stream, 1);
        if (silentStage == SilentStage.Challenge)
        {
            stageReached.TrySetResult();
            await WaitForClientCloseAsync(stream);
            return;
        }

        var modulus = Convert.FromHexString(
            "D2652EF10104A3DDC1219700EDFBD1E19F7678B4A4F6D5952634BD8BF1D60326322B5D32366DC25CB4E8E73AF4312A70D2DCAF2747EB89D7E88553EECD6A283D");
        var serverPublic = new byte[64];
        serverPublic[^1] = 125;
        await stream.WriteAsync(ArdServerFixture.EncodeChallenge(5, 64, modulus, serverPublic));
        _ = await ReadExactlyAsync(stream, 128 + 64);
        if (silentStage == SilentStage.Result)
        {
            stageReached.TrySetResult();
            await WaitForClientCloseAsync(stream);
            return;
        }

        var resultBytes = new List<byte>();
        AddUInt32(resultBytes, rejectionReason is null ? 0u : 7u);
        if (rejectionReason is not null)
        {
            AddUInt32(resultBytes, checked((uint)rejectionReason.Length));
            resultBytes.AddRange(rejectionReason);
        }

        await stream.WriteAsync(resultBytes.ToArray());
        stageReached.TrySetResult();
    }

    private static async Task RunFullArd889PointerSmokeServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.889\n"));
        Assert.Equal(Encoding.ASCII.GetBytes("RFB 003.889\n"), await ReadExactlyAsync(stream, 12));
        await stream.WriteAsync(new byte[] { 1, (byte)RfbSecurityType.AppleRemoteDesktop });
        Assert.Equal(new byte[] { (byte)RfbSecurityType.AppleRemoteDesktop }, await ReadExactlyAsync(stream, 1));

        var modulus = Convert.FromHexString(
            "D2652EF10104A3DDC1219700EDFBD1E19F7678B4A4F6D5952634BD8BF1D60326322B5D32366DC25CB4E8E73AF4312A70D2DCAF2747EB89D7E88553EECD6A283D");
        var serverPublic = new byte[64];
        serverPublic[^1] = 125;
        await stream.WriteAsync(ArdServerFixture.EncodeChallenge(5, 64, modulus, serverPublic));
        _ = await ReadExactlyAsync(stream, 128 + 64);
        await stream.WriteAsync(new byte[4]);

        Assert.Equal(new byte[] { 0xC1 }, await ReadExactlyAsync(stream, 1));
        var serverInit = new List<byte>();
        AddUInt16(serverInit, 4);
        AddUInt16(serverInit, 2);
        serverInit.AddRange(PixelFormat.WinArdBgra32.ToWireBytes());
        var extendedName = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(
            extendedName.AsSpan(2),
            (uint)ArdServerFlags.MayControl);
        Encoding.UTF8.GetBytes("Mac").CopyTo(extendedName, 23);
        AddUInt32(serverInit, checked((uint)extendedName.Length));
        serverInit.AddRange(extendedName);
        await stream.WriteAsync(serverInit.ToArray());

        Assert.Equal(0x21, (await ReadExactlyAsync(stream, 66))[0]);
        Assert.Equal(new byte[] { 0x0A, 0, 0, 1 }, await ReadExactlyAsync(stream, 4));
        Assert.Equal(new byte[] { 0x0D, 1, 0, 0, 0, 0, 0, 0 }, await ReadExactlyAsync(stream, 8));
        var declarationHeader = await ReadExactlyAsync(stream, 24);
        Assert.Equal((byte)0, declarationHeader[0]);
        Assert.Equal((byte)2, declarationHeader[20]);
        Assert.Equal(8, BinaryPrimitives.ReadUInt16BigEndian(declarationHeader.AsSpan(22)));
        Assert.Equal(
            [
                0, 0, 0, 6,
                0, 0, 0, 16,
                0, 0, 0, 0,
                0, 0, 0, 1,
                0xFF, 0xFF, 0xFF, 0x11,
            ],
            (await ReadExactlyAsync(stream, 32))[..20]);
        Assert.Equal(new byte[] { 5, 0, 0, 2, 0, 1 }, await ReadExactlyAsync(stream, 6));
        await AssertClientClosedWithoutAnotherRequestAsync(stream);
    }

    private static async Task RunCaptureServerAsync(
        TcpListener listener,
        bool sendEmptyUpdate = false,
        bool sendCursorOnly = false) =>
        await RunCaptureServerAsync(
            listener,
            sendEmptyUpdate
                ? CaptureScenario.Empty
                : sendCursorOnly
                    ? CaptureScenario.CursorOnly
                    : CaptureScenario.FullRaw);

    private static async Task RunCaptureServerAsync(
        TcpListener listener,
        CaptureScenario scenario,
        bool ard889 = false,
        byte[]? messagesBeforeUpdate = null)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var banner = Encoding.ASCII.GetBytes(ard889 ? "RFB 003.889\n" : "RFB 003.008\n");
        await stream.WriteAsync(banner);
        Assert.Equal(banner, await ReadExactlyAsync(stream, 12));
        await stream.WriteAsync(new byte[] { 1, (byte)RfbSecurityType.AppleRemoteDesktop });
        Assert.Equal(new byte[] { (byte)RfbSecurityType.AppleRemoteDesktop }, await ReadExactlyAsync(stream, 1));

        var modulus = Convert.FromHexString(
            "D2652EF10104A3DDC1219700EDFBD1E19F7678B4A4F6D5952634BD8BF1D60326322B5D32366DC25CB4E8E73AF4312A70D2DCAF2747EB89D7E88553EECD6A283D");
        var serverPublic = new byte[64];
        serverPublic[^1] = 125;
        await stream.WriteAsync(ArdServerFixture.EncodeChallenge(5, 64, modulus, serverPublic));
        _ = await ReadExactlyAsync(stream, 128 + 64);
        await stream.WriteAsync(new byte[4]);

        Assert.Equal(new byte[] { ard889 ? (byte)0xC1 : (byte)1 }, await ReadExactlyAsync(stream, 1));
        var initialWidth = scenario == CaptureScenario.PartialRawAcrossUpdates ? (ushort)2 : (ushort)1;
        var serverInit = new List<byte>();
        AddUInt16(serverInit, initialWidth);
        AddUInt16(serverInit, 1);
        serverInit.AddRange(PixelFormat.WinArdBgra32.ToWireBytes());
        var name = ard889 ? new byte[26] : Encoding.UTF8.GetBytes("Mac");
        if (ard889)
        {
            BinaryPrimitives.WriteUInt32BigEndian(name.AsSpan(2), (uint)ArdServerFlags.MayControl);
            Encoding.UTF8.GetBytes("Mac").CopyTo(name, 23);
        }
        AddUInt32(serverInit, checked((uint)name.Length));
        serverInit.AddRange(name);
        await stream.WriteAsync(serverInit.ToArray());

        if (ard889)
        {
            Assert.Equal(0x21, (await ReadExactlyAsync(stream, 66))[0]);
            Assert.Equal(new byte[] { 0x0A, 0, 0, 1 }, await ReadExactlyAsync(stream, 4));
            Assert.Equal(new byte[] { 0x0D, 1, 0, 0, 0, 0, 0, 0 }, await ReadExactlyAsync(stream, 8));
        }

        var declarationHeader = await ReadExactlyAsync(stream, 24);
        Assert.Equal((byte)0, declarationHeader[0]);
        Assert.Equal((byte)2, declarationHeader[20]);
        var encodingCount = ard889 ? 8 : 6;
        Assert.Equal(encodingCount, BinaryPrimitives.ReadUInt16BigEndian(declarationHeader.AsSpan(22)));
        Assert.Equal(
            [
                0, 0, 0, 6,
                0, 0, 0, 16,
                0, 0, 0, 0,
                0, 0, 0, 1,
                0xFF, 0xFF, 0xFF, 0x11,
            ],
            (await ReadExactlyAsync(stream, encodingCount * 4))[..20]);
        var request = await ReadExactlyAsync(stream, 10);
        Assert.Equal(CreateFullRequest(initialWidth, 1), request);

        if (messagesBeforeUpdate is not null)
        {
            await stream.WriteAsync(messagesBeforeUpdate);
        }

        if (scenario is CaptureScenario.CompleteOnSixtyFourthUpdate or CaptureScenario.IncompleteAfterSixtyFourUpdates)
        {
            for (var updateIndex = 0; updateIndex < 64; updateIndex++)
            {
                var complete = scenario == CaptureScenario.CompleteOnSixtyFourthUpdate && updateIndex == 63;
                if (complete)
                {
                    await WriteRectangleUpdateAsync(
                        stream,
                        0,
                        0,
                        1,
                        1,
                        RfbEncodingType.Raw,
                        [0, 0, 255, 0]);
                }
                else
                {
                    await stream.WriteAsync(new byte[] { 0, 0, 0, 0 });
                }

                if (updateIndex < 63)
                {
                    Assert.Equal(CreateFullRequest(1, 1), await ReadExactlyAsync(stream, 10));
                }
            }

            if (scenario == CaptureScenario.IncompleteAfterSixtyFourUpdates)
            {
                await AssertClientClosedWithoutAnotherRequestAsync(stream);
            }

            return;
        }

        if (scenario == CaptureScenario.Empty)
        {
            await stream.WriteAsync(new byte[] { 0, 0, 0, 0 });
            Assert.Equal(CreateFullRequest(initialWidth, 1), await ReadExactlyAsync(stream, 10));
            return;
        }

        if (scenario == CaptureScenario.CursorOnly)
        {
            var update = new List<byte> { 0, 0, 0, 1 };
            AddUInt16(update, 0);
            AddUInt16(update, 0);
            AddUInt16(update, 1);
            AddUInt16(update, 1);
            AddUInt32(update, unchecked((uint)(int)RfbEncodingType.Cursor));
            update.AddRange([0, 0, 255, 0, 0x80]);
            await stream.WriteAsync(update.ToArray());
            Assert.Equal(CreateFullRequest(initialWidth, 1), await ReadExactlyAsync(stream, 10));
            return;
        }

        if (scenario is CaptureScenario.DesktopSizeOnly or CaptureScenario.DesktopSizeThenFullRaw)
        {
            await WriteRectangleUpdateAsync(stream, 0, 0, 2, 1, RfbEncodingType.DesktopSize, []);
            Assert.Equal(CreateFullRequest(2, 1), await ReadExactlyAsync(stream, 10));
            if (scenario == CaptureScenario.DesktopSizeOnly)
            {
                return;
            }

            await WriteRectangleUpdateAsync(
                stream,
                0,
                0,
                2,
                1,
                RfbEncodingType.Raw,
                [0, 0, 255, 0, 0, 255, 0, 0]);
            return;
        }

        if (scenario == CaptureScenario.PartialRawAcrossUpdates)
        {
            await WriteRectangleUpdateAsync(stream, 0, 0, 1, 1, RfbEncodingType.Raw, [0, 0, 255, 0]);
            Assert.Equal(CreateFullRequest(2, 1), await ReadExactlyAsync(stream, 10));
            await WriteRectangleUpdateAsync(stream, 1, 0, 1, 1, RfbEncodingType.Raw, [0, 255, 0, 0]);
            return;
        }

        await WriteRectangleUpdateAsync(stream, 0, 0, 1, 1, RfbEncodingType.Raw, [0, 0, 255, 0]);
    }

    private static async Task WriteRectangleUpdateAsync(
        Stream stream,
        ushort x,
        ushort y,
        ushort width,
        ushort height,
        RfbEncodingType encoding,
        byte[] payload)
    {
        var update = new List<byte> { 0, 0, 0, 1 };
        AddUInt16(update, x);
        AddUInt16(update, y);
        AddUInt16(update, width);
        AddUInt16(update, height);
        AddUInt32(update, unchecked((uint)(int)encoding));
        update.AddRange(payload);
        await stream.WriteAsync(update.ToArray());
    }

    private static byte[] CreateFullRequest(ushort width, ushort height)
    {
        var request = new List<byte> { 3, 0, 0, 0, 0, 0 };
        AddUInt16(request, width);
        AddUInt16(request, height);
        return request.ToArray();
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var received = await stream.ReadAsync(buffer.AsMemory(offset));
            if (received == 0)
            {
                throw new EndOfStreamException("The probe closed before completing the scripted exchange.");
            }

            offset += received;
        }

        return buffer;
    }

    private static async Task WaitForClientCloseAsync(Stream stream)
    {
        var buffer = new byte[1];
        try
        {
            while (await stream.ReadAsync(buffer) != 0)
            {
            }
        }
        catch (IOException)
        {
        }
    }

    private static async Task AssertClientClosedWithoutAnotherRequestAsync(Stream stream)
    {
        var buffer = new byte[1];
        try
        {
            Assert.Equal(0, await stream.ReadAsync(buffer));
        }
        catch (IOException)
        {
        }
    }

    private static int GetPort(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static void AddUInt32(List<byte> destination, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        destination.AddRange(bytes.ToArray());
    }

    private static void AddUInt16(List<byte> destination, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        destination.AddRange(bytes.ToArray());
    }

    public enum SilentStage
    {
        Banner,
        Challenge,
        Result,
    }

    private enum CaptureScenario
    {
        FullRaw,
        Empty,
        CursorOnly,
        DesktopSizeOnly,
        DesktopSizeThenFullRaw,
        PartialRawAcrossUpdates,
        CompleteOnSixtyFourthUpdate,
        IncompleteAfterSixtyFourUpdates,
    }

    private sealed class FakePasswordConsole(
        bool treatControlCAsInput,
        params ConsoleKeyInfo[] keys) : IPasswordConsole
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);
        private bool _treatControlCAsInput = treatControlCAsInput;
        private int _readKeyCount;

        public List<bool> TreatControlCAsInputAssignments { get; } = [];

        public int? TreatControlCAsInputExceptionAssignment { get; init; }

        public int? ReadKeyExceptionAfter { get; init; }

        public bool IsInputRedirected => false;

        public bool TreatControlCAsInput
        {
            get => _treatControlCAsInput;
            set
            {
                TreatControlCAsInputAssignments.Add(value);
                if (TreatControlCAsInputAssignments.Count == TreatControlCAsInputExceptionAssignment)
                {
                    throw new IOException("Simulated console state failure.");
                }

                _treatControlCAsInput = value;
            }
        }

        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            if (_readKeyCount++ == ReadKeyExceptionAfter)
            {
                throw new IOException("Simulated console read failure.");
            }

            return _keys.Dequeue();
        }

        public void Write(string value)
        {
        }

        public void WriteLine()
        {
        }
    }
}
