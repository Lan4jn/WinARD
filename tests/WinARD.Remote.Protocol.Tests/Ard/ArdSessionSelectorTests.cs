using System.Buffers.Binary;
using System.Text;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Ard;

public sealed class ArdSessionSelectorTests
{
    [Fact]
    public async Task Allowed_connect_to_console_writes_command_one_with_the_server_username()
    {
        const string username = "alice-你";
        var stream = new ScriptedDuplexStream(
            [.. SessionInfo(1u << 1, Encoding.UTF8.GetBytes(username)), .. SessionResult(0)]);

        await ArdSessionSelector.SelectConsoleAsync(stream, ProtocolLimits.Default, CancellationToken.None);

        var command = stream.WrittenBytes;
        Assert.Equal(74, command.Length);
        Assert.Equal([0x00, 0x48, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00], command[..10]);
        Assert.Equal(Encoding.UTF8.GetBytes(username), command[10..(10 + Encoding.UTF8.GetByteCount(username))]);
        Assert.All(command[(10 + Encoding.UTF8.GetByteCount(username))..], value => Assert.Equal(0, value));
        Assert.Equal(1, stream.WriteCount);
    }

    [Fact]
    public async Task Pending_result_variants_continue_until_granted_after_pending_without_rewriting_command()
    {
        var stream = new ScriptedDuplexStream(
            [
                .. SessionInfo(1u << 1, "alice"u8.ToArray()),
                .. SessionResult(2),
                .. SessionResult(3, extra: [0xAA, 0xBB]),
                .. SessionResult(4),
            ]);

        await ArdSessionSelector.SelectConsoleAsync(stream, ProtocolLimits.Default, CancellationToken.None);

        Assert.Equal(1, stream.WriteCount);
        Assert.Equal(74, stream.WrittenBytes.Length);
        Assert.Equal(stream.InputLength, stream.BytesRead);
    }

    [Fact]
    public async Task Request_console_is_used_when_connect_to_console_is_not_allowed()
    {
        var stream = new ScriptedDuplexStream(
            [.. SessionInfo(1u << 0, "alice"u8.ToArray()), .. SessionResult(0)]);

        await ArdSessionSelector.SelectConsoleAsync(stream, ProtocolLimits.Default, CancellationToken.None);

        Assert.Equal(0, stream.WrittenBytes[8]);
    }

    [Fact]
    public async Task No_supported_command_throws_unavailable_without_writing()
    {
        var stream = new ScriptedDuplexStream(SessionInfo(1u << 2, "private-user"u8.ToArray()));

        var exception = await Assert.ThrowsAsync<ArdSessionCommandUnavailableException>(() =>
            ArdSessionSelector.SelectConsoleAsync(stream, ProtocolLimits.Default, CancellationToken.None));

        Assert.DoesNotContain("private-user", exception.ToString());
        Assert.Equal(0, stream.WriteCount);
        Assert.Empty(stream.WrittenBytes);
    }

    [Fact]
    public async Task Denied_result_preserves_only_the_safe_status()
    {
        const string username = "sensitive-user";
        const uint status = 0x11223344;
        var stream = new ScriptedDuplexStream(
            [.. SessionInfo(1u << 1, Encoding.UTF8.GetBytes(username)), .. SessionResult(status)]);

        var exception = await Assert.ThrowsAsync<ArdSessionDeniedException>(() =>
            ArdSessionSelector.SelectConsoleAsync(stream, ProtocolLimits.Default, CancellationToken.None));

        Assert.Equal(status, exception.Status);
        Assert.DoesNotContain(username, exception.ToString());
    }

    [Theory]
    [MemberData(nameof(MalformedSessionInfoMessages))]
    public async Task Malformed_session_info_is_wrapped_without_payload_disclosure(byte[] message, ProtocolLimits limits)
    {
        var stream = new ScriptedDuplexStream(message);

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdSessionSelector.SelectConsoleAsync(stream, limits, CancellationToken.None));

        Assert.Equal("Apple Remote Desktop session response was malformed.", exception.Message);
        Assert.Empty(stream.WrittenBytes);
    }

    [Theory]
    [MemberData(nameof(MalformedSessionResults))]
    public async Task Malformed_session_result_is_wrapped_without_payload_disclosure(byte[] result, ProtocolLimits limits)
    {
        var stream = new ScriptedDuplexStream(
            [.. SessionInfo(1u << 1, "alice"u8.ToArray()), .. result]);

        var exception = await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdSessionSelector.SelectConsoleAsync(stream, limits, CancellationToken.None));

        Assert.Equal("Apple Remote Desktop session response was malformed.", exception.Message);
        Assert.Equal(1, stream.WriteCount);
    }

    [Fact]
    public async Task Sixty_four_pending_results_fail_before_reading_a_sixty_fifth_result()
    {
        var input = new List<byte>();
        input.AddRange(SessionInfo(1u << 1, "alice"u8.ToArray()));
        for (var index = 0; index < 64; index++)
        {
            input.AddRange(SessionResult(2));
        }

        var acceptedInputLength = input.Count;
        input.AddRange(SessionResult(0));
        var stream = new ScriptedDuplexStream(input.ToArray());

        await Assert.ThrowsAsync<ArdSessionMalformedException>(() =>
            ArdSessionSelector.SelectConsoleAsync(stream, ProtocolLimits.Default, CancellationToken.None));

        Assert.Equal(acceptedInputLength, stream.BytesRead);
        Assert.Equal(1, stream.WriteCount);
    }

    [Fact]
    public async Task Username_ends_at_the_first_nul_and_ignores_following_bytes()
    {
        byte[] usernameField = [.. "alice"u8.ToArray(), 0, 0xFF, .. "hidden"u8.ToArray()];
        var stream = new ScriptedDuplexStream(
            [.. SessionInfo(1u << 1, usernameField), .. SessionResult(0)]);

        await ArdSessionSelector.SelectConsoleAsync(stream, ProtocolLimits.Default, CancellationToken.None);

        Assert.Equal("alice"u8.ToArray(), stream.WrittenBytes[10..15]);
        Assert.All(stream.WrittenBytes[15..], value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Pre_cancelled_operation_fails_before_reading_or_writing()
    {
        var stream = new ScriptedDuplexStream(
            [.. SessionInfo(1u << 1, "alice"u8.ToArray()), .. SessionResult(0)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArdSessionSelector.SelectConsoleAsync(stream, ProtocolLimits.Default, cancellation.Token));

        Assert.Equal(0, stream.ReadCount);
        Assert.Equal(0, stream.WriteCount);
    }

    [Fact]
    public async Task Cancellation_interrupts_a_pending_result_read()
    {
        var stream = new ScriptedDuplexStream(
            SessionInfo(1u << 1, "alice"u8.ToArray()),
            blockWhenInputEnds: true);
        using var cancellation = new CancellationTokenSource();

        var selection = ArdSessionSelector.SelectConsoleAsync(
            stream,
            ProtocolLimits.Default,
            cancellation.Token);
        await stream.WriteObserved.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.Equal(1, stream.WriteCount);
    }

    [Fact]
    public async Task Selection_does_not_flush_or_dispose_the_stream()
    {
        var stream = new ScriptedDuplexStream(
            [.. SessionInfo(1u << 1, "alice"u8.ToArray()), .. SessionResult(0)]);

        await ArdSessionSelector.SelectConsoleAsync(stream, ProtocolLimits.Default, CancellationToken.None);

        Assert.Equal(0, stream.FlushCount);
        Assert.Equal(0, stream.DisposeCount);
    }

    [Fact]
    public async Task Null_arguments_fail_before_io()
    {
        var stream = new ScriptedDuplexStream(Array.Empty<byte>());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ArdSessionSelector.SelectConsoleAsync(null!, ProtocolLimits.Default, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ArdSessionSelector.SelectConsoleAsync(stream, null!, CancellationToken.None));

        Assert.Equal(0, stream.ReadCount);
        Assert.Equal(0, stream.WriteCount);
    }

    public static TheoryData<byte[], ProtocolLimits> MalformedSessionInfoMessages => new()
    {
        { SizedBody(9, new byte[9]), ProtocolLimits.Default },
        { SizedBody(17, Array.Empty<byte>()), Limits(16) },
        { SessionInfo(1u << 1, "alice"u8.ToArray(), version: 2), ProtocolLimits.Default },
        { SizedBody(10, new byte[5]), ProtocolLimits.Default },
        { SessionInfo(1u << 1, [0xC3, 0x28]), ProtocolLimits.Default },
    };

    public static TheoryData<byte[], ProtocolLimits> MalformedSessionResults => new()
    {
        { SizedBody(5, new byte[5]), ProtocolLimits.Default },
        { SizedBody(17, Array.Empty<byte>()), Limits(16) },
        { SessionResult(0, version: 2), ProtocolLimits.Default },
        { SizedBody(6, new byte[3]), ProtocolLimits.Default },
    };

    private static ProtocolLimits Limits(int maxMessageBytes) => new(maxMessageBytes, 1024);

    private static byte[] SessionInfo(uint allowedCommands, byte[] username, ushort version = 1)
    {
        var body = new byte[10 + username.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, version);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(2), allowedCommands);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(6), 0);
        username.CopyTo(body, 10);
        return SizedBody(checked((ushort)body.Length), body);
    }

    private static byte[] SessionResult(uint status, ushort version = 1, byte[]? extra = null)
    {
        extra ??= Array.Empty<byte>();
        var body = new byte[6 + extra.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, version);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(2), status);
        extra.CopyTo(body, 6);
        return SizedBody(checked((ushort)body.Length), body);
    }

    private static byte[] SizedBody(ushort declaredSize, byte[] body)
    {
        var message = new byte[2 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(message, declaredSize);
        body.CopyTo(message, 2);
        return message;
    }

    private sealed class ScriptedDuplexStream : Stream
    {
        private readonly MemoryStream _input;
        private readonly MemoryStream _output = new();
        private readonly bool _blockWhenInputEnds;
        private readonly TaskCompletionSource _writeObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ScriptedDuplexStream(byte[] input, bool blockWhenInputEnds = false)
        {
            _input = new MemoryStream(input, writable: false);
            _blockWhenInputEnds = blockWhenInputEnds;
            InputLength = input.Length;
        }

        public int BytesRead { get; private set; }
        public int DisposeCount { get; private set; }
        public int FlushCount { get; private set; }
        public int InputLength { get; }
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public Task WriteObserved => _writeObserved.Task;
        public byte[] WrittenBytes => _output.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => FlushCount++;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (_input.Position < _input.Length)
            {
                var read = _input.Read(buffer.Span);
                BytesRead += read;
                return ValueTask.FromResult(read);
            }

            return _blockWhenInputEnds
                ? new ValueTask<int>(WaitForCancellationAsync(cancellationToken))
                : ValueTask.FromResult(0);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            _output.Write(buffer.Span);
            _writeObserved.TrySetResult();
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            if (disposing)
            {
                _input.Dispose();
                _output.Dispose();
            }

            base.Dispose(disposing);
        }

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
