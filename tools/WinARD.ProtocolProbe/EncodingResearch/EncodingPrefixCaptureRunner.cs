using System.Net.Sockets;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.Initialization;
using WinARD.Remote.Protocol.IO;

namespace WinARD.ProtocolProbe.EncodingResearch;

public delegate Task<IReadOnlyList<EncodingPrefixCapture>> EncodingPrefixCaptureOperation(
    string host,
    int port,
    ISecretMaterial username,
    ISecretMaterial password,
    int candidateEncodingId,
    string outputDirectory,
    bool syntheticScreenConfirmed,
    CancellationToken cancellationToken);

public sealed class EncodingPrefixCaptureRunner
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(30);
    private const int MaximumActivationUpdates = 8;
    private const int MaximumPrefixLength = 64 * 1024;
    private readonly TimeSpan _operationTimeout;
    private readonly Func<string, CancellationToken, Task> _confirmSample;

    public EncodingPrefixCaptureRunner()
        : this(DefaultOperationTimeout, ConfirmSampleAtConsoleAsync)
    {
    }

    public EncodingPrefixCaptureRunner(TimeSpan operationTimeout)
        : this(operationTimeout, ConfirmSampleAtConsoleAsync)
    {
    }

    internal EncodingPrefixCaptureRunner(
        TimeSpan operationTimeout,
        Func<string, CancellationToken, Task> confirmSample)
    {
        if (operationTimeout <= TimeSpan.Zero || operationTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        _operationTimeout = operationTimeout;
        _confirmSample = confirmSample ?? throw new ArgumentNullException(nameof(confirmSample));
    }

    public async Task<IReadOnlyList<EncodingPrefixCapture>> RunAsync(
        string host,
        int port,
        ISecretMaterial username,
        ISecretMaterial password,
        int candidateEncodingId,
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
        if (candidateEncodingId is not (1002 or 1001))
        {
            throw new ArgumentOutOfRangeException(nameof(candidateEncodingId));
        }
        EncodingPrefixCaptureFile.EnsureDestinationAvailable(outputDirectory);
        var fullOutputDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var parentDirectory = Directory.GetParent(fullOutputDirectory)
            ?? throw new IOException("The encoding prefix output directory must have a parent directory.");
        Directory.CreateDirectory(parentDirectory.FullName);
        var stagingDirectory = fullOutputDirectory + $".staging-{Guid.NewGuid():N}";
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            EncodingPrefixCaptureFile.RestrictStagingDirectory(stagingDirectory);
            var captures = new List<EncodingPrefixCapture>(EncodingPrefixCaptureFile.RequiredCaptureVariantNames.Count);
            foreach (var sampleName in EncodingPrefixCaptureFile.RequiredCaptureVariantNames)
            {
                await _confirmSample(sampleName, cancellationToken).ConfigureAwait(false);
                var capture = await CaptureOneWithTimeoutAsync(
                    host, port, username, password, candidateEncodingId, cancellationToken)
                    .ConfigureAwait(false);
                await EncodingPrefixCaptureFile.WriteSampleAsync(
                    stagingDirectory,
                    candidateEncodingId,
                    sampleName,
                    capture,
                    syntheticScreenConfirmed,
                    cancellationToken).ConfigureAwait(false);
                captures.Add(capture);
            }

            await EncodingPrefixCaptureFile.WriteSetManifestAsync(
                stagingDirectory,
                candidateEncodingId,
                captures,
                cancellationToken).ConfigureAwait(false);
            await EncodingPrefixCaptureFile.VerifyCaptureSetAsync(
                stagingDirectory,
                candidateEncodingId,
                captures,
                cancellationToken).ConfigureAwait(false);
            EncodingPrefixCaptureFile.ValidatePublishPaths(
                stagingDirectory,
                parentDirectory.FullName,
                fullOutputDirectory);
            Directory.Move(stagingDirectory, fullOutputDirectory);
            return captures;
        }
        catch (Exception exception)
        {
            TryCleanupStaging(stagingDirectory, exception);
            throw;
        }
    }

    private static void TryCleanupStaging(string stagingDirectory, Exception? primaryException)
    {
        try
        {
            if (File.GetAttributes(stagingDirectory).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(stagingDirectory);
            }
            else
            {
                DeleteReparseChildrenWithoutFollowing(stagingDirectory);
                Directory.Delete(stagingDirectory, recursive: true);
            }
            if (primaryException is not null)
            {
                primaryException.Data["EncodingPrefixStagingCleanup"] = "succeeded";
            }
        }
#pragma warning disable CA1031 // Best-effort cleanup must never replace the primary capture failure.
        catch (Exception cleanupException)
#pragma warning restore CA1031
        {
            if (primaryException is null)
            {
                throw;
            }

            primaryException.Data["EncodingPrefixStagingCleanup"] = "failed";
            primaryException.Data["EncodingPrefixStagingCleanupException"] = cleanupException.GetType().Name;
        }
    }

    private static void DeleteReparseChildrenWithoutFollowing(string directory)
    {
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(child);
            }
            else
            {
                DeleteReparseChildrenWithoutFollowing(child);
            }
        }
    }

    private static async Task ConfirmSampleAtConsoleAsync(
        string sampleName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "Each synthetic sample requires interactive visual confirmation before network capture.");
        }

        Console.Write($"Display only the synthetic '{sampleName}' sample, then type its name to confirm: ");
        var confirmation = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(confirmation, sampleName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Synthetic sample '{sampleName}' was not confirmed; capture did not start.");
        }

    }

    private async Task<EncodingPrefixCapture> CaptureOneWithTimeoutAsync(
        string host,
        int port,
        ISecretMaterial username,
        ISecretMaterial password,
        int candidateEncodingId,
        CancellationToken cancellationToken)
    {
        using var networkCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        networkCancellation.CancelAfter(_operationTimeout);
        try
        {
            return await CaptureOneAsync(
                host, port, username, password, candidateEncodingId, networkCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && networkCancellation.IsCancellationRequested)
        {
            throw new ProbeTimeoutException(exception);
        }
    }

    private static async Task<EncodingPrefixCapture> CaptureOneAsync(
        string host,
        int port,
        ISecretMaterial username,
        ISecretMaterial password,
        int candidateEncodingId,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var networkStream = client.GetStream();
        var handshake = await RfbHandshake.NegotiateAsync(networkStream, cancellationToken)
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
            cancellationToken).ConfigureAwait(false);
        await using var transport = new ArdEncryptedStream(networkStream, ProtocolLimits.Default);
        await using var encryption = new ArdSessionEncryption(transport, authentication);
        var server = await RfbSessionInitializer.InitializeAsync(
            transport,
            handshake,
            ProtocolLimits.Default,
            encryption,
            cancellationToken).ConfigureAwait(false);

        using var framebuffer = new Framebuffer(server.Width, server.Height, ProtocolLimits.Default);
        await using var updates = FramebufferUpdateReader.CreateSession(
            framebuffer,
            PixelFormat.WinArdBgra32,
            encryption.CreateDecoder());
        for (var updateIndex = 0;
             updateIndex < MaximumActivationUpdates && !transport.IsEncrypted;
             updateIndex++)
        {
            await WriteFullRequestAsync(transport, framebuffer, cancellationToken)
            .ConfigureAwait(false);
            await ReadInitialUpdateAsync(
                transport,
                updates,
                cancellationToken).ConfigureAwait(false);
            await encryption.CompleteFramebufferUpdateAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (!transport.IsEncrypted)
        {
            throw new RfbProtocolException(
                $"ARD session encryption did not activate within {MaximumActivationUpdates} framebuffer updates.");
        }

        await RfbSessionInitializer.WriteSetEncodingsAsync(
            transport,
            [candidateEncodingId, (int)RfbEncodingType.Zlib, (int)RfbEncodingType.Zrle, (int)RfbEncodingType.Raw],
            cancellationToken).ConfigureAwait(false);
        await WriteFullRequestAsync(transport, framebuffer, cancellationToken).ConfigureAwait(false);
        return await EncodingPrefixReader.ReadAsync(
            transport,
            candidateEncodingId,
            MaximumPrefixLength,
            checked((ushort)framebuffer.Width),
            checked((ushort)framebuffer.Height),
            token => WriteFullRequestAsync(transport, framebuffer, token),
            cancellationToken).ConfigureAwait(false);
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
