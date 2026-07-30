using System.Buffers.Binary;
using System.Text;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.IO;
using WinARD.Testing.Rfb;
using WinARD.Testing.Streams;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Handshake;

public sealed class RfbHandshakeTests
{
    [Theory]
    [InlineData("RFB 003.003\n")]
    [InlineData("RFB 003.007\n")]
    [InlineData("RFB 003.008\n")]
    public async Task Negotiates_supported_versions(string serverBanner)
    {
        await using var server = serverBanner is "RFB 003.003\n"
            ? FakeRfbServer.ForVersion(serverBanner, (uint)RfbSecurityType.AppleRemoteDesktop)
            : FakeRfbServer.ForVersion(serverBanner, (byte)RfbSecurityType.AppleRemoteDesktop);

        var result = await RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None);

        Assert.Equal(serverBanner, server.ReceivedVersion);
        Assert.Equal(RfbSecurityType.AppleRemoteDesktop, result.SecurityType);
        Assert.Equal(serverBanner, result.Version.Banner);
        Assert.Equal((byte)0xa5, await new RfbReader(server.ClientStream, ProtocolLimits.Default).ReadByteAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Negotiates_apple_003_889_and_preserves_its_protocol_identity()
    {
        await using var server = FakeRfbServer.ForVersion(
            "RFB 003.889\n",
            (byte)RfbSecurityType.AppleRemoteDesktop);

        var result = await RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None);

        Assert.Equal(RfbVersion.V3_889, result.Version);
        Assert.Equal(RfbSecurityType.AppleRemoteDesktop, result.SecurityType);
        Assert.Equal("RFB 003.889\n", server.ReceivedVersion);
        Assert.Equal((byte)RfbSecurityType.AppleRemoteDesktop, server.ReceivedBytes[12]);
    }

    [Theory]
    [InlineData("RFB 003.007\n", new byte[] { 30, 1, 2 })]
    [InlineData("RFB 003.008\n", new byte[] { 1, 2, 30 })]
    public async Task Negotiates_37_and_38_by_selecting_ard_from_any_list_position(string serverBanner, byte[] securityTypes)
    {
        await using var server = FakeRfbServer.ForVersion(serverBanner, securityTypes);

        var result = await RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None);

        Assert.Equal(RfbSecurityType.AppleRemoteDesktop, result.SecurityType);
        Assert.Equal(13, server.ReceivedBytes.Length);
        Assert.Equal((byte)RfbSecurityType.AppleRemoteDesktop, server.ReceivedBytes[12]);
    }

    [Fact]
    public async Task Negotiates_33_without_writing_a_security_selection()
    {
        await using var server = FakeRfbServer.ForVersion("RFB 003.003\n", (uint)RfbSecurityType.AppleRemoteDesktop);

        var result = await RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None);

        Assert.Equal(RfbSecurityType.AppleRemoteDesktop, result.SecurityType);
        Assert.Equal(12, server.ReceivedBytes.Length);
        Assert.Equal((byte)0xa5, await new RfbReader(server.ClientStream, ProtocolLimits.Default).ReadByteAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("XYZ 003.008\n")]
    [InlineData("RFB 003.008\r\n")]
    [InlineData("RFB 003.009\n")]
    [InlineData("RFB 004.000\n")]
    public async Task Rejects_malformed_or_unsupported_server_banners(string serverBanner)
    {
        await using var server = FakeRfbServer.ForBytes(Encoding.ASCII.GetBytes(serverBanner));

        var exception = await Record.ExceptionAsync(() => RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.NotNull(exception);
        Assert.True(exception is RfbProtocolException or UnsupportedRfbVersionException);
    }

    [Fact]
    public async Task Rejects_short_server_banner()
    {
        var banner = Encoding.ASCII.GetBytes("RFB 003.00");
        await using var server = FakeRfbServer.ForBytes(banner);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal(RfbHandshakeStage.VersionBanner, exception.Failure?.HandshakeStage);
        Assert.Equal(12, exception.Failure?.ExpectedByteCount);
        Assert.Equal(banner.Length, exception.Failure?.ActualByteCount);
    }

    [Fact]
    public async Task Reports_complete_malformed_banner_without_exporting_banner_bytes()
    {
        await using var server = FakeRfbServer.ForBytes(Encoding.ASCII.GetBytes("XYZ 003.008\n"));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal(RfbHandshakeStage.VersionParse, exception.Failure?.HandshakeStage);
        Assert.Equal(12, exception.Failure?.ExpectedByteCount);
        Assert.Equal(12, exception.Failure?.ActualByteCount);
    }

    [Fact]
    public async Task Reports_missing_security_type_count()
    {
        await using var server = FakeRfbServer.ForBytes(Encoding.ASCII.GetBytes("RFB 003.889\n"));

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal(RfbHandshakeStage.SecurityTypeCount, exception.Failure?.HandshakeStage);
        Assert.Equal(1, exception.Failure?.ExpectedByteCount);
        Assert.Equal(0, exception.Failure?.ActualByteCount);
    }

    [Fact]
    public async Task Reports_truncated_security_type_list()
    {
        await using var server = FakeRfbServer.ForBytes(
            [.. Encoding.ASCII.GetBytes("RFB 003.889\n"), 3, 1]);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal(RfbHandshakeStage.SecurityTypes, exception.Failure?.HandshakeStage);
        Assert.Equal(3, exception.Failure?.ExpectedByteCount);
        Assert.Equal(1, exception.Failure?.ActualByteCount);
    }

    [Theory]
    [InlineData("RFB 003.007\n")]
    [InlineData("RFB 003.008\n")]
    public async Task Reports_37_and_38_rejection_reason(string serverBanner)
    {
        await using var server = FakeRfbServer.ForBytes(BuildRejection(serverBanner, "Denied\nplease retry"));

        var exception = await Assert.ThrowsAsync<RfbConnectionRejectedException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal("Denied\\u000Aplease retry", exception.Reason);
        Assert.Contains("Denied", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_apple_003_889_rejection_reason_using_rfb_38_semantics()
    {
        await using var server = FakeRfbServer.ForBytes(BuildRejection("RFB 003.889\n", "Alias denied\nretry"));

        var exception = await Assert.ThrowsAsync<RfbConnectionRejectedException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal(RfbVersion.V3_889, exception.Version);
        Assert.Equal("Alias denied\\u000Aretry", exception.Reason);
        Assert.Equal("RFB 003.889\n", server.ReceivedVersion);
    }

    [Fact]
    public async Task Reports_33_rejection_reason()
    {
        await using var server = FakeRfbServer.ForBytes(BuildRejection("RFB 003.003\n", "No access"));

        var exception = await Assert.ThrowsAsync<RfbConnectionRejectedException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal("No access", exception.Reason);
    }

    [Fact]
    public async Task Escapes_control_characters_in_rejection_reason_and_message()
    {
        const string reason = "carriage\rline\nescape\u001bc0\u0001c1\u0085line\u2028paragraph\u2029bidi\u202Atail";
        await using var server = FakeRfbServer.ForBytes(BuildRejection("RFB 003.008\n", reason));

        var exception = await Assert.ThrowsAsync<RfbConnectionRejectedException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        const string expected = "carriage\\u000Dline\\u000Aescape\\u001Bc0\\u0001c1\\u0085line\\u2028paragraph\\u2029bidi\\u202Atail";
        Assert.Equal(expected, exception.Reason);
        Assert.Equal($"The server rejected the connection: {expected}", exception.Message);
        Assert.DoesNotContain('\r', exception.Message);
        Assert.DoesNotContain('\n', exception.Message);
        Assert.DoesNotContain('\u001b', exception.Message);
        Assert.DoesNotContain('\u0001', exception.Message);
        Assert.DoesNotContain('\u0085', exception.Message);
        Assert.DoesNotContain('\u2028', exception.Message);
        Assert.DoesNotContain('\u2029', exception.Message);
        Assert.DoesNotContain('\u202a', exception.Message);
    }

    [Fact]
    public async Task Uses_replacement_character_for_invalid_utf8_rejection_reason()
    {
        await using var server = FakeRfbServer.ForBytes(BuildRejection("RFB 003.008\n", [0xc3, 0x28]));

        var exception = await Assert.ThrowsAsync<RfbConnectionRejectedException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal("\ufffd(", exception.Reason);
    }

    [Fact]
    public async Task Truncates_rejection_reason_at_a_rune_boundary_after_reading_all_payload()
    {
        var sourceReason = string.Concat(Enumerable.Repeat("😀", 4097));
        var sourceBytes = Encoding.UTF8.GetBytes(sourceReason);
        await using var server = FakeRfbServer.ForBytes(BuildRejection("RFB 003.008\n", sourceReason));

        var exception = await Assert.ThrowsAsync<RfbConnectionRejectedException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.True(exception.IsReasonTruncated);
        Assert.Equal(4096, exception.Reason.Length);
        Assert.Equal(2048, exception.Reason.EnumerateRunes().Count());
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 2048)), exception.Reason);
        Assert.Contains("truncated", exception.Message, StringComparison.Ordinal);
        Assert.Equal(17 + sourceBytes.Length, server.ServerBytesRead);
    }

    [Fact]
    public async Task Reports_an_empty_rejection_reason()
    {
        await using var server = FakeRfbServer.ForBytes(BuildRejection("RFB 003.008\n", string.Empty));

        var exception = await Assert.ThrowsAsync<RfbConnectionRejectedException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal(string.Empty, exception.Reason);
        Assert.Equal("The server rejected the connection.", exception.Message);
    }

    [Theory]
    [InlineData("RFB 003.007\n", new byte[] { 1, 2 })]
    [InlineData("RFB 003.008\n", new byte[] { 1, 99 })]
    public async Task Rejects_offered_security_types_without_ard(string serverBanner, byte[] securityTypes)
    {
        await using var server = FakeRfbServer.ForVersion(serverBanner, securityTypes);

        var exception = await Assert.ThrowsAsync<UnsupportedSecurityTypeException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal(securityTypes.Select(value => (uint)value), exception.OfferedTypes);
        var offered = Assert.IsAssignableFrom<IList<uint>>(exception.OfferedTypes);
        Assert.Throws<NotSupportedException>(() => offered[0] = 255);
        Assert.Equal(securityTypes.Select(value => (uint)value), exception.OfferedTypes);
        Assert.Equal(Encoding.ASCII.GetBytes(serverBanner), server.ReceivedBytes);
    }

    [Fact]
    public async Task Reads_a_255_entry_security_list_and_selects_ard()
    {
        var securityTypes = Enumerable.Repeat((byte)1, 254).Append((byte)RfbSecurityType.AppleRemoteDesktop).ToArray();
        await using var server = FakeRfbServer.ForVersion("RFB 003.008\n", securityTypes);

        await RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None);

        Assert.Equal((byte)0xa5, await new RfbReader(server.ClientStream, ProtocolLimits.Default).ReadByteAsync(CancellationToken.None));
    }

    [Fact]
    public void Fake_server_rejects_a_256_entry_security_list()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FakeRfbServer.ForVersion("RFB 003.008\n", new byte[256]));
    }

    [Fact]
    public void Fake_server_requires_a_uint_security_type_for_rfb_33()
    {
        Assert.Throws<ArgumentException>(() =>
            FakeRfbServer.ForVersion("RFB 003.003\n", new byte[] { (byte)RfbSecurityType.AppleRemoteDesktop }));
    }

    [Fact]
    public async Task Fake_server_does_not_release_security_data_before_client_banner_is_verified()
    {
        await using var server = FakeRfbServer.ForVersion("RFB 003.008\n", (byte)RfbSecurityType.AppleRemoteDesktop);
        var banner = new byte[12];
        Assert.Equal(12, await server.ClientStream.ReadAsync(banner));

        var security = new byte[1];
        var pendingRead = server.ClientStream.ReadAsync(security).AsTask();
        Assert.False(pendingRead.IsCompleted);

        await server.ClientStream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        Assert.Equal(1, await pendingRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, security[0]);
    }

    [Fact]
    public async Task Fake_server_requires_37_selection_before_releasing_sentinel()
    {
        await using var server = FakeRfbServer.ForVersion("RFB 003.007\n", (byte)RfbSecurityType.AppleRemoteDesktop);
        var banner = new byte[12];
        await server.ClientStream.ReadExactlyAsync(banner);
        await server.ClientStream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.007\n"));
        var security = new byte[2];
        await server.ClientStream.ReadExactlyAsync(security);

        var sentinel = new byte[1];
        var pendingRead = server.ClientStream.ReadAsync(sentinel).AsTask();
        Assert.False(pendingRead.IsCompleted);

        await server.ClientStream.WriteAsync(new byte[] { (byte)RfbSecurityType.AppleRemoteDesktop });
        Assert.Equal(1, await pendingRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal((byte)0xa5, sentinel[0]);
    }

    [Fact]
    public async Task Fake_server_disposal_ends_a_read_waiting_for_client_banner()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var server = FakeRfbServer.ForVersion("RFB 003.008\n", (byte)RfbSecurityType.AppleRemoteDesktop);
            await server.ClientStream.ReadExactlyAsync(new byte[12]);
            var pendingRead = server.ClientStream.ReadAsync(new byte[1]).AsTask();

            await server.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => pendingRead.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task Fake_server_disposal_ends_a_read_waiting_for_security_selection()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var server = FakeRfbServer.ForVersion("RFB 003.008\n", (byte)RfbSecurityType.AppleRemoteDesktop);
            await server.ClientStream.ReadExactlyAsync(new byte[12]);
            await server.ClientStream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"));
            await server.ClientStream.ReadExactlyAsync(new byte[2]);
            var pendingRead = server.ClientStream.ReadAsync(new byte[1]).AsTask();

            await server.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => pendingRead.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task Fake_server_returns_zero_for_an_empty_read_while_waiting_for_client_banner()
    {
        await using var server = FakeRfbServer.ForVersion("RFB 003.008\n", (byte)RfbSecurityType.AppleRemoteDesktop);
        await server.ClientStream.ReadExactlyAsync(new byte[12]);

        Assert.Equal(0, await server.ClientStream.ReadAsync(Memory<byte>.Empty).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Fake_server_returns_zero_for_an_empty_read_before_sentinel()
    {
        await using var server = FakeRfbServer.ForVersion("RFB 003.008\n", (byte)RfbSecurityType.AppleRemoteDesktop);
        await server.ClientStream.ReadExactlyAsync(new byte[12]);
        await server.ClientStream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        await server.ClientStream.ReadExactlyAsync(new byte[2]);
        await server.ClientStream.WriteAsync(new byte[] { (byte)RfbSecurityType.AppleRemoteDesktop });

        Assert.Equal(0, await server.ClientStream.ReadAsync(Memory<byte>.Empty).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal((byte)0xa5, await new RfbReader(server.ClientStream, ProtocolLimits.Default).ReadByteAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Fake_server_releases_33_sentinel_without_a_selection()
    {
        await using var server = FakeRfbServer.ForVersion("RFB 003.003\n", (uint)RfbSecurityType.AppleRemoteDesktop);
        var banner = new byte[12];
        await server.ClientStream.ReadExactlyAsync(banner);
        await server.ClientStream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.003\n"));
        var security = new byte[4];
        await server.ClientStream.ReadExactlyAsync(security);

        var sentinel = new byte[1];
        Assert.Equal(1, await server.ClientStream.ReadAsync(sentinel));
        Assert.Equal((byte)0xa5, sentinel[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.ClientStream.WriteAsync(new byte[] { (byte)RfbSecurityType.AppleRemoteDesktop }).AsTask());
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(0x00000100u)]
    public async Task Rejects_33_security_types_other_than_ard(uint securityType)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("RFB 003.003\n"));
        bytes.AddRange([
            (byte)(securityType >> 24),
            (byte)(securityType >> 16),
            (byte)(securityType >> 8),
            (byte)securityType,
        ]);
        await using var server = FakeRfbServer.ForBytes(bytes.ToArray());

        var exception = await Assert.ThrowsAsync<UnsupportedSecurityTypeException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal(new[] { securityType }, exception.OfferedTypes);
    }

    [Fact]
    public async Task Rejects_reason_length_above_limit_before_reading_reason_payload()
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        bytes.Add(0);
        bytes.AddRange([0, 0, 0, 13]);
        bytes.AddRange(Encoding.UTF8.GetBytes("thirteenchars"));
        await using var server = FakeRfbServer.ForBytes(bytes.ToArray());

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, new ProtocolLimits(12, 1024), CancellationToken.None));

        Assert.Equal("RFB 003.008\n", server.ReceivedVersion);
        Assert.Equal(17, server.ServerBytesRead);
    }

    [Fact]
    public async Task Rejects_uint_max_rejection_reason_length_before_reading_payload()
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        bytes.Add(0);
        bytes.AddRange([0xff, 0xff, 0xff, 0xff]);
        await using var server = FakeRfbServer.ForBytes(bytes.ToArray());

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal(17, server.ServerBytesRead);
    }

    [Fact]
    public async Task Wraps_premature_end_of_stream_while_reading_reason()
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        bytes.Add(0);
        bytes.AddRange([0, 0, 0, 3]);
        bytes.Add((byte)'x');
        await using var server = FakeRfbServer.ForBytes(bytes.ToArray());

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));
    }

    [Fact]
    public async Task Wraps_premature_end_of_stream_while_reading_security_type_list()
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        bytes.Add(2);
        bytes.Add(1);
        await using var server = FakeRfbServer.ForBytes(bytes.ToArray());

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_null_stream()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            RfbHandshake.NegotiateAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Propagates_pre_cancelled_token()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RfbHandshake.NegotiateAsync(Stream.Null, cancellation.Token));
    }

    [Fact]
    public async Task Propagates_cancellation_to_pending_read()
    {
        await using var stream = new BlockingReadStream();
        using var cancellation = new CancellationTokenSource();
        var negotiateTask = RfbHandshake.NegotiateAsync(stream, cancellation.Token);

        try
        {
            await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => negotiateTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, stream.ReceivedCancellationToken);
        }
        finally
        {
            await stream.DisposeAsync();
            await ObserveCompletionAsync(negotiateTask);
        }
    }

    [Fact]
    public async Task Does_not_flush_or_dispose_stream()
    {
        await using var server = FakeRfbServer.ForVersion("RFB 003.008\n", (byte)RfbSecurityType.AppleRemoteDesktop);

        await RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None);

        Assert.Equal(0, server.FlushCount);
        Assert.False(server.IsDisposed);
    }

    [Fact]
    public async Task Staged_handshake_sequencing_is_reliable_across_repeated_runs()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var server = FakeRfbServer.ForVersion("RFB 003.008\n", (byte)RfbSecurityType.AppleRemoteDesktop);

            await RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None);

            Assert.Equal((byte)0xa5, await new RfbReader(server.ClientStream, ProtocolLimits.Default).ReadByteAsync(CancellationToken.None));
        }
    }

    private static byte[] BuildRejection(string versionBanner, string reason) =>
        BuildRejection(versionBanner, Encoding.UTF8.GetBytes(reason));

    private static byte[] BuildRejection(string versionBanner, byte[] reasonBytes)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes(versionBanner));
        if (versionBanner is "RFB 003.003\n")
        {
            bytes.AddRange([0, 0, 0, 0]);
        }
        else
        {
            bytes.Add(0);
        }

        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)reasonBytes.Length);
        bytes.AddRange(length.ToArray());
        bytes.AddRange(reasonBytes);
        return bytes.ToArray();
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
