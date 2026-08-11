using System.Net.Sockets;
using WinARD.ProtocolProbe.RdmCapture;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.Initialization;
using WinARD.Remote.Protocol.IO;

namespace WinARD.ProtocolProbe.EncodingResearch;

public delegate Task<EncodingPrefixCapture> EncodingPrefixCaptureOperation(
    string host,
    int port,
    ISecretMaterial username,
    ISecretMaterial password,
    string baselineCapturePath,
    string adaptiveCapturePath,
    string outputDirectory,
    bool syntheticScreenConfirmed,
    CancellationToken cancellationToken);

public sealed class EncodingPrefixCaptureRunner
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(30);
    private const int MaximumActivationUpdates = 8;
    private const int MaximumPrefixLength = 64 * 1024;
    private readonly TimeSpan _operationTimeout;

    public EncodingPrefixCaptureRunner()
        : this(DefaultOperationTimeout)
    {
    }

    public EncodingPrefixCaptureRunner(TimeSpan operationTimeout)
    {
        if (operationTimeout <= TimeSpan.Zero || operationTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        _operationTimeout = operationTimeout;
    }

    public async Task<EncodingPrefixCapture> RunAsync(
        string host,
        int port,
        ISecretMaterial username,
        ISecretMaterial password,
        string baselineCapturePath,
        string adaptiveCapturePath,
        string outputDirectory,
        bool syntheticScreenConfirmed,
        CancellationToken cancellationToken)
    {
        EncodingPrefixCaptureFile.RequireSyntheticScreenConfirmation(syntheticScreenConfirmed);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineCapturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(adaptiveCapturePath);
        EncodingPrefixCaptureFile.EnsureDestinationAvailable(outputDirectory);

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(_operationTimeout);
        try
        {
            var baseline = await RdmCaptureFile.ReadAsync(
                baselineCapturePath,
                operationCancellation.Token).ConfigureAwait(false);
            var adaptive = await RdmCaptureFile.ReadAsync(
                adaptiveCapturePath,
                operationCancellation.Token).ConfigureAwait(false);
            var candidateEncodingId = RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(baseline, adaptive);

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, operationCancellation.Token).ConfigureAwait(false);
            await using var networkStream = client.GetStream();
            var handshake = await RfbHandshake.NegotiateAsync(networkStream, operationCancellation.Token)
                .ConfigureAwait(false);
            if (handshake.Version != RfbVersion.V3_889
                || handshake.SecurityType != RfbSecurityType.AppleRemoteDesktop)
            {
                throw new RfbProtocolException(
                    "Encoding prefix capture requires an Apple RFB 3.889 security type 30 session.");
            }

            var authentication = await new ArdAuthenticator().AuthenticateAsync(
                networkStream,
                handshake.Version,
                username,
                password,
                operationCancellation.Token).ConfigureAwait(false);
            await using var transport = new ArdEncryptedStream(networkStream, ProtocolLimits.Default);
            await using var encryption = new ArdSessionEncryption(transport, authentication);
            var server = await RfbSessionInitializer.InitializeAsync(
                transport,
                handshake,
                ProtocolLimits.Default,
                encryption,
                operationCancellation.Token).ConfigureAwait(false);

            using var framebuffer = new Framebuffer(server.Width, server.Height, ProtocolLimits.Default);
            await using var updates = FramebufferUpdateReader.CreateSession(
                framebuffer,
                PixelFormat.WinArdBgra32,
                encryption.CreateDecoder());
            for (var updateIndex = 0;
                 updateIndex < MaximumActivationUpdates && !transport.IsEncrypted;
                 updateIndex++)
            {
                await WriteFullRequestAsync(transport, framebuffer, operationCancellation.Token)
                    .ConfigureAwait(false);
                await ReadInitialUpdateAsync(
                    transport,
                    updates,
                    operationCancellation.Token).ConfigureAwait(false);
                await encryption.CompleteFramebufferUpdateAsync(operationCancellation.Token)
                    .ConfigureAwait(false);
            }

            if (!transport.IsEncrypted)
            {
                throw new RfbProtocolException(
                    $"ARD session encryption did not activate within {MaximumActivationUpdates} framebuffer updates.");
            }

            await RfbSessionInitializer.WriteSetEncodingsAsync(
                transport,
                [candidateEncodingId, (int)RfbEncodingType.Raw],
                operationCancellation.Token).ConfigureAwait(false);
            await WriteFullRequestAsync(transport, framebuffer, operationCancellation.Token)
                .ConfigureAwait(false);
            var capture = await EncodingPrefixReader.ReadAsync(
                transport,
                candidateEncodingId,
                MaximumPrefixLength,
                operationCancellation.Token).ConfigureAwait(false);
            await EncodingPrefixCaptureFile.WriteAsync(
                outputDirectory,
                capture,
                syntheticScreenConfirmed,
                operationCancellation.Token).ConfigureAwait(false);
            return capture;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && operationCancellation.IsCancellationRequested)
        {
            throw new ProbeTimeoutException(exception);
        }
    }

    private static async Task ReadInitialUpdateAsync(
        Stream stream,
        FramebufferUpdateSession updates,
        CancellationToken cancellationToken)
    {
        var reader = new RfbReader(stream, ProtocolLimits.Default);
        while (true)
        {
            var messageType = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (messageType == 0)
            {
                _ = await updates.ApplyBodyAsync(stream, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!ArdServerMessage.IsZeroPayloadControl(messageType))
            {
                throw new RfbProtocolException(
                    $"Expected FramebufferUpdate message type 0, received {messageType}.");
            }
        }
    }

    private static Task WriteFullRequestAsync(
        Stream stream,
        Framebuffer framebuffer,
        CancellationToken cancellationToken) =>
        RfbSessionInitializer.WriteFramebufferUpdateRequestAsync(
            stream,
            incremental: false,
            0,
            0,
            checked((ushort)framebuffer.Width),
            checked((ushort)framebuffer.Height),
            cancellationToken);
}
