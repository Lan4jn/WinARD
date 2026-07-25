using System.Net.Sockets;
using WinARD.Remote.Protocol.Authentication;
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
        await RunAsync(host, port, username, password, null, cancellationToken);

    public async Task<ProbeResult> RunAsync(
        string host,
        int port,
        ISecretMaterial username,
        ISecretMaterial password,
        string? captureFirstFramePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

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
            if (captureFirstFramePath is null)
            {
                return new ProbeResult(handshake.Version, handshake.SecurityType);
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(captureFirstFramePath);
            var server = await RfbSessionInitializer.InitializeAsync(
                stream,
                ProtocolLimits.Default,
                operationCancellation.Token);
            using var framebuffer = new Framebuffer(server.Width, server.Height, ProtocolLimits.Default);
            var coverage = new PixelCoverage(server.Width, server.Height);
            var dirtyRects = new List<FramebufferRect>();
            for (var updateCount = 0; updateCount < MaximumInitialFramebufferUpdates; updateCount++)
            {
                await WriteFullFramebufferUpdateRequestAsync(stream, framebuffer, operationCancellation.Token);
                var update = await FramebufferUpdateReader.ApplyAsync(
                    stream,
                    framebuffer,
                    PixelFormat.WinArdBgra32,
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

}
