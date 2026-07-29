using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Ard;

public sealed class ArdStateChangeReaderTests
{
    [Fact]
    public void Reader_exposes_public_body_read_api()
    {
        var readerType = typeof(ArdClientInitFlags).Assembly.GetType(
            "WinARD.Remote.Protocol.Ard.ArdStateChangeReader");

        Assert.NotNull(readerType);
        Assert.NotNull(readerType.GetMethod("ReadBodyAsync"));
    }

    [Fact]
    public async Task Minimal_body_preserves_nonzero_padding_flags_and_status()
    {
        var stateChange = await ArdStateChangeReader.ReadBodyAsync(
            Reader(Body(0xA5, 4, 0x1234, 0x0001)),
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(0xA5, stateChange.Padding);
        Assert.Equal(4, stateChange.PayloadSize);
        Assert.Equal(0x1234, stateChange.Flags);
        Assert.Equal(0x0001, stateChange.Status);
        Assert.Equal(0, stateChange.ExtraPayloadLength);
    }

    [Fact]
    public async Task Extra_payload_is_fully_consumed_without_reading_the_next_byte()
    {
        const byte sentinel = 0xEE;
        var stream = new MemoryStream([.. Body(0x7F, 6, 0xABCD, 0x0102, [0xDE, 0xAD]), sentinel]);
        var reader = new RfbReader(stream, ProtocolLimits.Default);

        var stateChange = await ArdStateChangeReader.ReadBodyAsync(
            reader,
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal(2, stateChange.ExtraPayloadLength);
        Assert.Equal(sentinel, await reader.ReadByteAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Payload_sizes_smaller_than_fixed_fields_are_malformed(int size)
    {
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            ArdStateChangeReader.ReadBodyAsync(
                Reader([0, (byte)(size >> 8), (byte)size]),
                ProtocolLimits.Default,
                CancellationToken.None).AsTask());

        Assert.Equal((RfbProtocolFailureKind)6, exception.Failure?.Kind);
        Assert.Equal((RfbProtocolReadStage)6, exception.Failure?.ReadStage);
        Assert.Equal((byte)0x14, exception.Failure?.ServerMessageType);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0 })]
    [InlineData(new byte[] { 0, 0 })]
    public async Task Truncated_header_preserves_eof_kind_and_adds_state_change_context(byte[] body)
    {
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            ArdStateChangeReader.ReadBodyAsync(Reader(body), ProtocolLimits.Default, CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal((RfbProtocolReadStage)6, exception.Failure?.ReadStage);
        Assert.Equal((byte)0x14, exception.Failure?.ServerMessageType);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 4, 0x12 })]
    [InlineData(new byte[] { 0, 0, 6, 0x12, 0x34, 0x00, 0x01, 0xDE })]
    public async Task Truncated_payload_or_extra_preserves_eof_kind_and_adds_state_change_context(byte[] body)
    {
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            ArdStateChangeReader.ReadBodyAsync(Reader(body), ProtocolLimits.Default, CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal((RfbProtocolReadStage)7, exception.Failure?.ReadStage);
        Assert.Equal((byte)0x14, exception.Failure?.ServerMessageType);
    }

    [Fact]
    public async Task Declared_message_size_above_limit_is_rejected_before_payload_read()
    {
        var limits = new ProtocolLimits(6, 6);
        await using var stream = new MemoryStream(Body(0, 4, 0, 0));
        var reader = new RfbReader(stream, limits);
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            ArdStateChangeReader.ReadBodyAsync(
                reader,
                limits,
                CancellationToken.None).AsTask());

        Assert.Equal(3, stream.Position);
        Assert.Equal((RfbProtocolFailureKind)6, exception.Failure?.Kind);
        Assert.Equal((RfbProtocolReadStage)6, exception.Failure?.ReadStage);
        Assert.Equal((byte)0x14, exception.Failure?.ServerMessageType);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(0x7FFF)]
    public async Task All_status_values_are_accepted_and_preserved_as_raw_ushort(int status)
    {
        var stateChange = await ArdStateChangeReader.ReadBodyAsync(
            Reader(Body(0, 4, 0, checked((ushort)status))),
            ProtocolLimits.Default,
            CancellationToken.None);

        Assert.Equal((ushort)status, stateChange.Status);
    }

    [Fact]
    public async Task Precancelled_token_is_propagated_without_wrapping()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArdStateChangeReader.ReadBodyAsync(
                Reader(Body(0, 4, 0, 0)),
                ProtocolLimits.Default,
                cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static RfbReader Reader(byte[] body) =>
        new(new MemoryStream(body), ProtocolLimits.Default);

    private static byte[] Body(
        byte padding,
        ushort size,
        ushort flags,
        ushort status,
        byte[]? extra = null)
    {
        var body = new byte[3 + size];
        body[0] = padding;
        body[1] = (byte)(size >> 8);
        body[2] = (byte)size;
        body[3] = (byte)(flags >> 8);
        body[4] = (byte)flags;
        body[5] = (byte)(status >> 8);
        body[6] = (byte)status;
        extra?.CopyTo(body, 7);
        return body;
    }
}
