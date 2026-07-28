using System.Buffers.Binary;
using System.Text;
using WinARD.Remote.Protocol.Clipboard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;
using WinARD.Testing.Streams;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Clipboard;

public sealed class ClipboardProtocolTests
{
    [Fact]
    public void Encode_client_cut_text_normalizes_crlf_and_writes_utf8_message()
    {
        var message = ClipboardProtocol.EncodeClientCutText("一\r\n二");

        Assert.Equal(6, message[0]);
        Assert.Equal([0, 0, 0], message[1..4]);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(4)));
        Assert.Equal("一\n二", Encoding.UTF8.GetString(message.AsSpan(8)));
    }

    [Fact]
    public void Encode_raw_text_returns_normalized_utf8_without_protocol_header()
    {
        Assert.Equal(
            Encoding.UTF8.GetBytes("alpha\nbeta"),
            ClipboardProtocol.EncodeText("alpha\r\nbeta"));
    }

    [Fact]
    public async Task Async_writer_sends_one_complete_client_cut_text_message()
    {
        await using var stream = new MemoryStream();
        var protocol = new ClipboardProtocol(new RfbWriter(stream));

        await protocol.WriteClientCutTextAsync("hello", CancellationToken.None);

        Assert.Equal(
            [6, 0, 0, 0, 0, 0, 0, 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o'],
            stream.ToArray());
    }

    [Fact]
    public void Encode_rejects_utf8_payload_above_limit()
    {
        var exception = Assert.Throws<RfbProtocolException>(() =>
            ClipboardProtocol.EncodeClientCutText("€", maxUtf8Bytes: 2));

        Assert.Contains("2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_rejects_invalid_utf16_with_stable_protocol_exception()
    {
        foreach (var text in new[]
        {
            new string((char)0xD800, 1),
            new string((char)0xDC00, 1),
            string.Concat("a", new string((char)0xD800, 1), "b"),
        })
        {
            var exception = Assert.Throws<RfbProtocolException>(() =>
                ClipboardProtocol.EncodeText(text));

            Assert.Contains("UTF-16", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Precancelled_write_does_not_scan_or_allocate_clipboard_payload()
    {
        await using var stream = new MemoryStream();
        var protocol = new ClipboardProtocol(new RfbWriter(stream));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            protocol.WriteClientCutTextAsync("\uD800", cancellation.Token).AsTask());

        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public void Default_utf8_limit_is_one_mebibyte()
    {
        Assert.Equal(1_048_576, ClipboardProtocol.DefaultMaxUtf8Bytes);
        Assert.Equal(
            1_048_576,
            ClipboardProtocol.EncodeText(new string('a', 1_048_576)).Length);
        Assert.Throws<RfbProtocolException>(() =>
            ClipboardProtocol.EncodeText(new string('a', 1_048_577)));
    }

    [Fact]
    public void Decode_client_cut_text_rejects_invalid_type_padding_trailing_and_invalid_utf8()
    {
        Assert.Throws<RfbProtocolException>(() =>
            ClipboardProtocol.DecodeClientCutText([3, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Throws<RfbProtocolException>(() =>
            ClipboardProtocol.DecodeClientCutText([6, 1, 0, 0, 0, 0, 0, 0]));
        Assert.Throws<RfbProtocolException>(() =>
            ClipboardProtocol.DecodeClientCutText([6, 0, 0, 0, 0, 0, 0, 0, 1]));
        Assert.Throws<RfbProtocolException>(() =>
            ClipboardProtocol.DecodeClientCutText([6, 0, 0, 0, 0, 0, 0, 2, 0xC3, 0x28]));
    }

    [Fact]
    public void Decode_client_cut_text_rejects_truncated_or_oversized_length()
    {
        Assert.Throws<RfbProtocolException>(() =>
            ClipboardProtocol.DecodeClientCutText([6, 0, 0, 0, 0, 0, 0, 2, (byte)'a']));
        Assert.Throws<RfbProtocolException>(() =>
            ClipboardProtocol.DecodeClientCutText(
                [6, 0, 0, 0, 0, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c'],
                maxUtf8Bytes: 2));
        Assert.Throws<RfbProtocolException>(() =>
            ClipboardProtocol.DecodeClientCutText([6, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public async Task Async_reader_decodes_client_and_server_cut_text()
    {
        var clientReader = Reader(ClipboardProtocol.EncodeClientCutText("a\r\nb"));
        var serverReader = Reader(ServerCutText("c\r\nd"));

        Assert.Equal(
            "a\nb",
            await ClipboardProtocol.ReadClientCutTextAsync(clientReader, CancellationToken.None));
        Assert.Equal(
            "c\nd",
            await ClipboardProtocol.ReadServerCutTextAsync(serverReader, CancellationToken.None));
    }

    [Fact]
    public async Task Server_reader_type_eof_has_header_stage_without_inventing_type()
    {
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            ClipboardProtocol.ReadServerCutTextAsync(Reader([]), CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ClipboardHeader, exception.Failure?.ReadStage);
        Assert.Null(exception.Failure?.ServerMessageType);
    }

    [Fact]
    public async Task Server_reader_type_mismatch_reports_actual_type()
    {
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            ClipboardProtocol.ReadServerCutTextAsync(Reader([0x08]), CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.MalformedClipboard, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ClipboardHeader, exception.Failure?.ReadStage);
        Assert.Equal((byte)0x08, exception.Failure?.ServerMessageType);
    }

    [Fact]
    public async Task Async_reader_rejects_malicious_length_before_payload_read()
    {
        var message = new byte[10];
        message[0] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), 1_048_577);
        message[8] = 0xDE;
        message[9] = 0xAD;
        await using var stream = new MemoryStream(message);

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            ClipboardProtocol.ReadClientCutTextAsync(
                new RfbReader(stream, ProtocolLimits.Default),
                CancellationToken.None).AsTask());

        Assert.Equal(8, stream.Position);
        Assert.Equal(RfbProtocolFailureKind.MalformedClipboard, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ClipboardHeader, exception.Failure?.ReadStage);
        Assert.Null(exception.Failure?.ServerMessageType);
    }

    [Fact]
    public async Task Async_reader_rejects_truncation_and_invalid_utf8()
    {
        var truncated = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            ClipboardProtocol.ReadClientCutTextAsync(
                Reader([6, 0, 0, 0, 0, 0, 0, 2, (byte)'a']),
                CancellationToken.None).AsTask());
        var invalidUtf8 = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            ClipboardProtocol.ReadClientCutTextAsync(
                Reader([6, 0, 0, 0, 0, 0, 0, 2, 0xC3, 0x28]),
                CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, truncated.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ClipboardPayload, truncated.Failure?.ReadStage);
        Assert.Null(truncated.Failure?.ServerMessageType);
        Assert.Equal(RfbProtocolFailureKind.MalformedClipboard, invalidUtf8.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ClipboardPayload, invalidUtf8.Failure?.ReadStage);
        Assert.Null(invalidUtf8.Failure?.ServerMessageType);
    }

    [Fact]
    public async Task Server_reader_reports_malformed_padding_as_header_without_content_fields()
    {
        const string secretMarker = "SECRET-MARKER-DO-NOT-CAPTURE";
        var message = ServerCutText(secretMarker);
        message[1] = 1;

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            ClipboardProtocol.ReadServerCutTextAsync(Reader(message), CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.MalformedClipboard, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ClipboardHeader, exception.Failure?.ReadStage);
        Assert.Equal((byte)3, exception.Failure?.ServerMessageType);
        Assert.Null(exception.Failure?.EncodingId);
        Assert.Null(exception.Failure?.RectangleIndex);
        Assert.DoesNotContain(secretMarker, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_payload_truncation_preserves_eof_kind_and_server_type()
    {
        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            ClipboardProtocol.ReadServerCutTextAsync(
                Reader([3, 0, 0, 0, 0, 0, 0, 2, (byte)'a']),
                CancellationToken.None).AsTask());

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
        Assert.Equal(RfbProtocolReadStage.ClipboardPayload, exception.Failure?.ReadStage);
        Assert.Equal((byte)3, exception.Failure?.ServerMessageType);
    }

    [Fact]
    public async Task Async_clipboard_operations_propagate_cancellation()
    {
        await using var stream = new BlockingReadStream();
        using var cancellation = new CancellationTokenSource();
        var read = ClipboardProtocol.ReadClientCutTextAsync(
            new RfbReader(stream, ProtocolLimits.Default),
            cancellation.Token).AsTask();
        await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            read.WaitAsync(TimeSpan.FromSeconds(5)));

        await using var output = new MemoryStream();
        var protocol = new ClipboardProtocol(new RfbWriter(output));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            protocol.WriteClientCutTextAsync("text", cancellation.Token).AsTask());
        Assert.Empty(output.ToArray());
    }

    private static RfbReader Reader(byte[] message) =>
        new(new MemoryStream(message), ProtocolLimits.Default);

    private static byte[] ServerCutText(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var message = new byte[payload.Length + 8];
        message[0] = 3;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), checked((uint)payload.Length));
        payload.CopyTo(message, 8);
        return message;
    }
}
