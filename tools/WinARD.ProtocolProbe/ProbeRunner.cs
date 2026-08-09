using System.Net.Sockets;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.Initialization;
using WinARD.Remote.Protocol.IO;

namespace WinARD.ProtocolProbe;

public sealed class ProbeRunner
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(30);
    private const int MaximumInitialFramebufferUpdates = 64;

    private readonly TimeSpan _operationTimeout;

    public ProbeRunner()
        : this(DefaultOperationTimeout)
    {
    }

    public ProbeRunner(TimeSpan operationTimeout)
    {
        if (operationTimeout <= TimeSpan.Zero || operationTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        _operationTimeout = operationTimeout;
    }

    public async Task<ProbeResult> RunAsync(
        string host,
        int port,
        ISecretMaterial username,
        ISecretMaterial password,
        CancellationToken cancellationToken) =>
        await RunRequestAsync(
            host,
            port,
            username,
            password,
            new ProbeRequest(ProbeMode.Authentication),
            cancellationToken);

    public async Task<ProbeResult> RunAsync(
        string host,
        int port,
        ISecretMaterial username,
        ISecretMaterial password,
        string? captureFirstFramePath,
        CancellationToken cancellationToken) =>
        await RunRequestAsync(
            host,
            port,
            username,
            password,
            captureFirstFramePath is null
                ? new ProbeRequest(ProbeMode.Authentication)
                : new ProbeRequest(ProbeMode.CaptureFirstFrame, captureFirstFramePath),
            cancellationToken);

    public async Task<ProbeResult> RunRequestAsync(
        string host,
        int port,
        ISecretMaterial username,
        ISecretMaterial password,
        ProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(_operationTimeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, operationCancellation.Token);
            await using var stream = client.GetStream();
            var handshake = await RfbHandshake.NegotiateAsync(stream, operationCancellation.Token);
            if (handshake.SecurityType != RfbSecurityType.AppleRemoteDesktop)
            {
                throw new RfbProtocolException("The server did not negotiate Apple Remote Desktop security type 30.");
            }

            await new ArdAuthenticator().AuthenticateAsync(
                stream,
                handshake.Version,
                username,
                password,
                operationCancellation.Token);
            if (request.Mode == ProbeMode.Authentication)
            {
                return new ProbeResult(handshake.Version, handshake.SecurityType);
            }

            if (request.Mode == ProbeMode.PointerSmoke)
            {
                var pointerServer = await RfbSessionInitializer.InitializeAsync(
                    stream,
                    handshake,
                    ProtocolLimits.Default,
                    operationCancellation.Token);
                var pointerSmoke = await PointerSmokeProbe.SendAsync(
                    stream,
                    pointerServer.Width,
                    pointerServer.Height,
                    operationCancellation.Token);
                return new ProbeResult(handshake.Version, handshake.SecurityType)
                {
                    PointerSmoke = pointerSmoke,
                };
            }

            if (request.Mode != ProbeMode.CaptureFirstFrame)
            {
                throw new ArgumentOutOfRangeException(nameof(request));
            }

            var captureFirstFramePath = request.CaptureFirstFramePath!;
            var server = await RfbSessionInitializer.InitializeAsync(
                stream,
                handshake,
                ProtocolLimits.Default,
                operationCancellation.Token);
            using var framebuffer = new Framebuffer(server.Width, server.Height, ProtocolLimits.Default);
            await using var framebufferUpdates = FramebufferUpdateReader.CreateSession(
                framebuffer,
                PixelFormat.WinArdBgra32);
            var coverage = new PixelCoverage(server.Width, server.Height);
            var dirtyRects = new List<FramebufferRect>();
            for (var updateCount = 0; updateCount < MaximumInitialFramebufferUpdates; updateCount++)
            {
                await WriteFullFramebufferUpdateRequestAsync(stream, framebuffer, operationCancellation.Token);
                var update = await ReadFramebufferUpdateAsync(
                    stream,
                    framebufferUpdates,
                    handshake.Version,
                    operationCancellation.Token);
                dirtyRects.AddRange(update.DirtyRects);
                if (update.DesktopResized)
                {
                    coverage.Reset(framebuffer.Width, framebuffer.Height);
                }

                foreach (var rectangle in update.PixelContentRects)
                {
                    coverage.Add(rectangle, operationCancellation.Token);
                }

                if (coverage.IsComplete)
                {
                    break;
                }
            }

            if (!coverage.IsComplete)
            {
                throw new RfbProtocolException(
                    $"The initial framebuffer did not become complete within {MaximumInitialFramebufferUpdates} updates.");
            }

            var fullPath = Path.GetFullPath(captureFirstFramePath);
            await FramebufferCaptureWriter.WriteAsync(fullPath, framebuffer, operationCancellation.Token);
            return new ProbeResult(
                handshake.Version,
                handshake.SecurityType,
                new ProbeCapture(fullPath, framebuffer.Width, framebuffer.Height, dirtyRects));
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && operationCancellation.IsCancellationRequested)
        {
            throw new ProbeTimeoutException(exception);
        }
    }

    private static async Task WriteFullFramebufferUpdateRequestAsync(
        Stream stream,
        Framebuffer framebuffer,
        CancellationToken cancellationToken) =>
        await RfbSessionInitializer.WriteFramebufferUpdateRequestAsync(
            stream,
            incremental: false,
            0,
            0,
            checked((ushort)framebuffer.Width),
            checked((ushort)framebuffer.Height),
            cancellationToken);

    private static async Task<FramebufferUpdateResult> ReadFramebufferUpdateAsync(
        Stream stream,
        FramebufferUpdateSession updates,
        RfbVersion version,
        CancellationToken cancellationToken)
    {
        var reader = new RfbReader(stream, ProtocolLimits.Default);
        while (true)
        {
            var messageType = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (messageType == 0)
            {
                return await updates.ApplyBodyAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            if (version == RfbVersion.V3_889 && ArdServerMessage.IsZeroPayloadControl(messageType))
            {
                continue;
            }

            throw new RfbProtocolException($"Expected FramebufferUpdate message type 0, received {messageType}.");
        }
    }

    private static void ValidateRequest(ProbeRequest request)
    {
        if (!Enum.IsDefined(request.Mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Mode,
                "The probe mode is not defined.");
        }

        if (request.Mode is ProbeMode.Authentication or ProbeMode.PointerSmoke)
        {
            if (request.OutputPath is not null)
            {
                throw new ArgumentException(
                    $"{request.Mode} mode must not specify a capture path.",
                    nameof(request));
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(request.CaptureFirstFramePath))
        {
            throw new ArgumentException(
                "CaptureFirstFrame mode requires a non-blank capture path.",
                nameof(request));
        }
    }

}
