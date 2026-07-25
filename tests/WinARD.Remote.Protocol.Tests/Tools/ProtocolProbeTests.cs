using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using WinARD.ProtocolProbe;
using WinARD.Remote.Protocol.Authentication;
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
    public async Task Capture_probe_initializes_requests_full_frame_and_writes_bmp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = RunCaptureServerAsync(listener);
        using var username = SecretMaterial.FromUtf8("capture-user");
        using var password = SecretMaterial.FromUtf8("capture-password");
        var path = Path.Combine(Path.GetTempPath(), $"winard-capture-{Guid.NewGuid():N}.bmp");
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
            var bmp = await File.ReadAllBytesAsync(path);
            Assert.Equal([0, 0, 255, 255], bmp[54..]);
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(path);
        }
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

    private static async Task RunCaptureServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        Assert.Equal(Encoding.ASCII.GetBytes("RFB 003.008\n"), await ReadExactlyAsync(stream, 12));
        await stream.WriteAsync(new byte[] { 1, (byte)RfbSecurityType.AppleRemoteDesktop });
        Assert.Equal(new byte[] { (byte)RfbSecurityType.AppleRemoteDesktop }, await ReadExactlyAsync(stream, 1));

        var modulus = Convert.FromHexString(
            "D2652EF10104A3DDC1219700EDFBD1E19F7678B4A4F6D5952634BD8BF1D60326322B5D32366DC25CB4E8E73AF4312A70D2DCAF2747EB89D7E88553EECD6A283D");
        var serverPublic = new byte[64];
        serverPublic[^1] = 125;
        await stream.WriteAsync(ArdServerFixture.EncodeChallenge(5, 64, modulus, serverPublic));
        _ = await ReadExactlyAsync(stream, 128 + 64);
        await stream.WriteAsync(new byte[4]);

        Assert.Equal(new byte[] { 1 }, await ReadExactlyAsync(stream, 1));
        var serverInit = new List<byte>();
        AddUInt16(serverInit, 1);
        AddUInt16(serverInit, 1);
        serverInit.AddRange(PixelFormat.WinArdBgra32.ToWireBytes());
        AddUInt32(serverInit, 3);
        serverInit.AddRange(Encoding.UTF8.GetBytes("Mac"));
        await stream.WriteAsync(serverInit.ToArray());

        var declarations = await ReadExactlyAsync(stream, 40);
        Assert.Equal((byte)0, declarations[0]);
        Assert.Equal((byte)2, declarations[20]);
        var request = await ReadExactlyAsync(stream, 10);
        Assert.Equal([3, 0, 0, 0, 0, 0, 0, 1, 0, 1], request);

        var update = new List<byte> { 0, 0, 0, 1 };
        AddUInt16(update, 0);
        AddUInt16(update, 0);
        AddUInt16(update, 1);
        AddUInt16(update, 1);
        AddUInt32(update, 0);
        update.AddRange([0, 0, 255, 0]);
        await stream.WriteAsync(update.ToArray());
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
