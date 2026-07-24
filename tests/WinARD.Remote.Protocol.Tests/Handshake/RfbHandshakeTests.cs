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
        await using var server = FakeRfbServer.ForVersion(serverBanner, (byte)RfbSecurityType.AppleRemoteDesktop);

        var result = await RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None);

        Assert.Equal(serverBanner, server.ReceivedVersion);
        Assert.Equal(RfbSecurityType.AppleRemoteDesktop, result.SecurityType);
        Assert.Equal(serverBanner, result.Version.Banner);
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
        await using var server = FakeRfbServer.ForVersion("RFB 003.003\n", (byte)RfbSecurityType.AppleRemoteDesktop);

        var result = await RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None);

        Assert.Equal(RfbSecurityType.AppleRemoteDesktop, result.SecurityType);
        Assert.Equal(12, server.ReceivedBytes.Length);
    }

    [Theory]
    [InlineData("XYZ 003.008\n")]
    [InlineData("RFB 003.008\r\n")]
    [InlineData("RFB 003.889\n")]
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
        await using var server = FakeRfbServer.ForBytes(Encoding.ASCII.GetBytes("RFB 003.00"));

        await Assert.ThrowsAsync<RfbProtocolException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));
    }

    [Theory]
    [InlineData("RFB 003.007\n")]
    [InlineData("RFB 003.008\n")]
    public async Task Reports_37_and_38_rejection_reason(string serverBanner)
    {
        await using var server = FakeRfbServer.ForBytes(BuildRejection(serverBanner, "Denied\nplease retry"));

        var exception = await Assert.ThrowsAsync<RfbConnectionRejectedException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal("Denied\nplease retry", exception.Reason);
        Assert.Contains("Denied", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_33_rejection_reason()
    {
        await using var server = FakeRfbServer.ForBytes(BuildRejection("RFB 003.003\n", "No access"));

        var exception = await Assert.ThrowsAsync<RfbConnectionRejectedException>(() =>
            RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));

        Assert.Equal("No access", exception.Reason);
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

    private static byte[] BuildRejection(string versionBanner, string reason)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes(versionBanner));
        var reasonBytes = Encoding.UTF8.GetBytes(reason);
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
