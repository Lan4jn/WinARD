using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using WinARD.Application.Ports;
using WinARD.Application.Quality;
using WinARD.Desktop.Services;
using WinARD.Domain.Security;
using WinARD.ProtocolProbe.EncodingResearch;
using WinARD.Remote.Protocol.Errors;
using WinARD.Security.WindowsCredentials;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--self-test"])
        {
            var header = new RfbProtocolFailureInfo(RfbProtocolFailureKind.UnsupportedEncoding,
                RfbProtocolReadStage.FramebufferRectangleHeader, EncodingId: 1011);
            if (!IsMvsHeader(header) || IsMvsHeader(header with { EncodingId = 1002 }) ||
                IsMvsHeader(header with { Kind = RfbProtocolFailureKind.DecoderFailure }) ||
                IsMvsHeader(header with { ReadStage = RfbProtocolReadStage.FramebufferRectanglePayload }) || IsMvsHeader(null))
                throw new IOException("MVS header classification failed.");
            await using var stream = new MemoryStream();
            await using var client = new RfbClient(stream);
            try
            {
                await client.ProbeMvsAsync(CancellationToken.None);
                throw new IOException("MVS probe accepted an uninitialized session.");
            }
            catch (InvalidOperationException) { }
            try
            {
                await client.CaptureCompleteMvsAsync("test", "test-dir", MvsCaptureMode.Both, CancellationToken.None);
                throw new IOException("Complete capture guard accepted an uninitialized session.");
            }
            catch (InvalidOperationException) { }
            foreach (var scale in new[] { 0.25d, 0.5d, 1d })
            {
                try
                {
                    await client.ProbeScaleAsync(scale, CancellationToken.None);
                    throw new IOException("Unsafe probe guard accepted an uninitialized session.");
                }
                catch (ArgumentOutOfRangeException) when (scale == 0.25d) { }
                catch (InvalidOperationException) when (scale is 0.5d or 1d) { }
            }
            if (stream.Length != 0 || StoreCategory("private-endpoint") != "unknown")
                throw new IOException("Probe safety self-test failed.");
            Console.WriteLine("SelfTest=Passed");
            return 0;
        }
        var mvs = args.Length > 0 && args[^1] == "--mvs-select";
        if (mvs) args = args[..^1];

        var captureCompleteMvs = false;
        var captureMvs = false;
        string? sampleName = null;
        string? sampleOutputDir = null;
        var captureMode = MvsCaptureMode.Both;

        if (args.Length >= 4 && args[^1] == "--confirm-synthetic")
        {
            if (args.Length >= 6 && args[^6] == "--capture-complete-mvs" && args[^3] == "--mode")
            {
                captureCompleteMvs = true;
                sampleName = args[^5];
                sampleOutputDir = args[^4];
                captureMode = args[^2].ToLowerInvariant() switch
                {
                    "both" => MvsCaptureMode.Both,
                    "setup" => MvsCaptureMode.SetupOnly,
                    "slice" => MvsCaptureMode.SliceOnly,
                    _ => throw new ArgumentException($"Invalid --mode '{args[^2]}', expected both, setup, or slice.")
                };
                args = args[..^6];
            }
            else if (args.Length >= 4 && args[^4] == "--capture-complete-mvs")
            {
                captureCompleteMvs = true;
                sampleName = args[^3];
                sampleOutputDir = args[^2];
                captureMode = MvsCaptureMode.Both;
                args = args[..^4];
            }
            else if (args.Length >= 4 && args[^4] == "--capture-mvs")
            {
                captureMvs = true;
                sampleName = args[^3];
                sampleOutputDir = args[^2];
                args = args[..^4];
            }
        }

        var selectedPort = 0;
        var byPort = args.Length == 3 && args[1] is "--port" or "--dns-port" &&
            int.TryParse(args[2], out selectedPort) && selectedPort is >= 1 and <= 65535;
        if (!byPort && (args.Length != 2 || (args[1] is not ("--inspect-saved" or "--run-single") && !Guid.TryParse(args[1], out _))))
        {
            Console.Error.WriteLine(
                "Usage: WinARD.ScaleProbe <existing-database> <--inspect-saved|--run-single|device-guid|--port port|--dns-port port> " +
                "[--capture-complete-mvs <sample-name> <output-dir> [--mode both|setup|slice] --confirm-synthetic | " +
                "--capture-mvs <sample-name> <output-dir> --confirm-synthetic | " +
                "--mvs-select]; --self-test");
            return 2;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; timeout.Cancel(); };
        try
        {
            await using var database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(args[0]), Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            await database.OpenAsync(timeout.Token);
            database.CreateFunction<string, bool>("is_dns_host", host => Uri.CheckHostName(host) == UriHostNameType.Dns);
            await using var query = database.CreateCommand();
            query.CommandText = "SELECT id,host,port,mac_username,transport_mode,credential_store,credential_key FROM devices";
            if (Guid.TryParse(args[1], out var deviceId))
            {
                query.CommandText += " WHERE id=$id";
                query.Parameters.AddWithValue("$id", deviceId.ToString("D"));
            }
            else if (byPort)
            {
                query.CommandText += " WHERE port=$port";
                if (args[1] == "--dns-port") query.CommandText += " AND is_dns_host(host)";
                query.Parameters.AddWithValue("$port", selectedPort);
            }
            await using var rows = await query.ExecuteReaderAsync(timeout.Token);
            if (args[1] == "--inspect-saved")
            {
                var count = 0;
                while (await rows.ReadAsync(timeout.Token))
                    Console.WriteLine($"DeviceIndex={++count} Id={rows.GetString(0)} Host={rows.GetString(1)} Port={rows.GetInt32(2)} SSH={rows.GetInt32(4) != 0} Store={StoreCategory(rows.IsDBNull(5) ? null : rows.GetString(5))}");
                Console.WriteLine($"SavedCount={count}");
                return 0;
            }
            if (!await rows.ReadAsync(timeout.Token)) throw new InvalidOperationException("Device unavailable.");
            if (rows.GetInt32(4) != 0 || rows.IsDBNull(5) || rows.GetString(5) != "windows")
            {
                Console.Error.WriteLine("NeedsExistingUi=CredentialsOrSsh");
                return 3;
            }
            var host = rows.GetString(1);
            var port = rows.GetInt32(2);
            var username = rows.GetString(3);
            var reference = CredentialReference.Create("windows", rows.GetString(6));
            if (await rows.ReadAsync(timeout.Token))
            {
                Console.Error.WriteLine("SelectionRequired=MultipleSavedDevices");
                return 3;
            }
            using var secret = await new WindowsCredentialStore().ReadAsync(
                reference, timeout.Token)
                ?? throw new InvalidOperationException("Saved credential unavailable.");
            using var socket = new TcpClient();
            await socket.ConnectAsync(host, port, timeout.Token);
            await using var client = new RfbClient(socket.GetStream(), requireArdAuthentication: true);
            await client.NegotiateAsync(timeout.Token);
            await client.AuthenticateAsync(username, secret, timeout.Token);
            await client.InitializeAsync(timeout.Token);
            Console.WriteLine($"Initialized={client.FramebufferSize.Width}x{client.FramebufferSize.Height}");
            await client.ConfigureBootstrapAsync(new QualityBootstrapSettings(
                RemotePixelFormatKind.Rgb565, [6, 16, 0, 1, -239, -223], QualityBootstrapReason.UserColor16),
                QualityBootstrapAttempt.Preferred, timeout.Token);
            var baselineSizes = await ObserveAsync(client, "baseline-100", timeout.Token);
            if (captureCompleteMvs)
            {
                var result = await client.CaptureCompleteMvsAsync(sampleName!, sampleOutputDir!, captureMode, timeout.Token);
                Console.WriteLine(
                    $"MvsCompleteCapture=Success Mode={captureMode} " +
                    $"SetupCaptured={(result.SetupCapture is not null)} " +
                    $"SliceCaptured={(result.SliceCapture is not null)} " +
                    $"SuccessorBoundaryValidated={result.SuccessorBoundaryValidated} " +
                    $"NextMessageType={result.NextMessageType}");
                if (result.SetupCapture is { } setup)
                {
                    Console.WriteLine(
                        $"SetupDetails DeclaredLength={setup.DeclaredLength} ActualLength={setup.ActualLength} SHA256={setup.PayloadSha256}");
                }
                if (result.SliceCapture is { } slice)
                {
                    Console.WriteLine(
                        $"SliceDetails Rectangle={slice.Rectangle.Width}x{slice.Rectangle.Height} " +
                        $"DeclaredLength={slice.DeclaredLength} ActualLength={slice.ActualLength} " +
                        $"Completeness={slice.Completeness} SHA256={slice.PayloadSha256}");
                }
                return 0;
            }
            if (captureMvs)
            {
                var capture = await client.CaptureMvsSampleAsync(sampleName!, sampleOutputDir!, timeout.Token);
                Console.WriteLine(
                    $"MvsSampleCaptured=True SampleName={capture.SampleName} " +
                    $"Rectangle={capture.Rectangle.Width}x{capture.Rectangle.Height} " +
                    $"PrefixLength={capture.PrefixLength} SHA256={capture.PayloadSha256} " +
                    $"Heuristics={capture.HeuristicSignature}");
                return 0;
            }
            if (mvs)
            {
                await client.ProbeMvsAsync(timeout.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(25));
                try
                {
                    for (var i = 0; i < 64; i++)
                    {
                        await client.RequestFramebufferUpdateAsync(false, deadline.Token);
                        var message = await client.ReceiveAsync(deadline.Token);
                        (message as IDisposable)?.Dispose();
                    }
                    Console.WriteLine("MvsSelection=NotObservedWithinMessageLimit");
                }
                catch (RfbProtocolException exception) when (IsMvsHeader(exception.Failure))
                {
                    Console.WriteLine("MvsSelection=HeaderObserved EncodingId=1011 DecoderImplemented=False");
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
                {
                    Console.WriteLine("MvsSelection=NotObservedWithinTimeout");
                }
                return 0;
            }
            var initialSize = baselineSizes.FirstOrDefault();
            Console.WriteLine("ScaleRequest=50");
            await client.ProbeScaleAsync(0.5, timeout.Token);
            var scale50Sizes = await ObserveAsync(client, "requested-50", timeout.Token);
            var scale50Confirmed = initialSize.Width > 0 && scale50Sizes.Count > 0 && scale50Sizes.All(s => s.Width < initialSize.Width);
            Console.WriteLine($"Scale50Result={(scale50Confirmed ? "Confirmed" : "Unconfirmed")}");
            Console.WriteLine("ScaleRequest=100");
            await client.ProbeScaleAsync(1, timeout.Token);
            var restore100Sizes = await ObserveAsync(client, "restored-100", timeout.Token);
            var restoredConfirmed = initialSize.Width > 0 && restore100Sizes.Contains(initialSize);
            Console.WriteLine($"ScaleRestoreResult={(restoredConfirmed ? "Confirmed" : "Unconfirmed")}");
            Console.WriteLine($"RoundTripResult={(scale50Confirmed && restoredConfirmed ? "Success" : "Unconfirmed")}");
            return 0;
        }
        catch (Exception exception)
        {
            // Exception messages may include the endpoint; emit only structured failure categories.
            Console.Error.WriteLine($"FailureType={exception.GetType().Name}");
            if (exception is EncodingCandidateNotObservedException candidate)
                Console.Error.WriteLine($"ObservedEncodingId={candidate.ObservedEncodingId}");
            if (exception is RfbProtocolException protocol)
                Console.Error.WriteLine($"FailureKind={protocol.Failure?.Kind} ReadStage={protocol.Failure?.ReadStage} EncodingId={protocol.Failure?.EncodingId} RectIndex={protocol.Failure?.RectangleIndex} Detail={protocol.Message}");
            if (exception is InvalidOperationException inv)
                Console.Error.WriteLine($"InvalidOpDetail={inv.Message}");
            if (exception is IOException io)
                Console.Error.WriteLine($"IoDetail={io.Message}");
            return 1;
        }
    }

    private static string StoreCategory(string? store) => store switch
    {
        "windows" or "vault" or "ask" or "transient" => store,
        null => "missing",
        _ => "unknown",
    };

    private static bool IsMvsHeader(RfbProtocolFailureInfo? failure) => failure is
    {
        Kind: RfbProtocolFailureKind.UnsupportedEncoding,
        ReadStage: RfbProtocolReadStage.FramebufferRectangleHeader,
        EncodingId: 1011,
    };


    private static async Task<IReadOnlyCollection<RemoteFramebufferSize>> ObserveAsync(
        RfbClient client, string stage, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var timer = Stopwatch.StartNew();
        var frames = 0;
        long bytes = 0;
        var sizes = new HashSet<RemoteFramebufferSize>();
        while (timer.Elapsed < TimeSpan.FromSeconds(6) || frames == 0)
        {
            await client.RequestFramebufferUpdateAsync(incremental: false, deadline.Token);
            while (true)
            {
                var message = await client.ReceiveAsync(deadline.Token);
                try
                {
                    if (message is RemoteCursorMessage cursor) { bytes += cursor.Statistics?.ReceivedSessionBytes ?? 0; break; }
                    if (message is not RemoteFramebufferMessage frame) continue;
                    bytes += frame.Statistics.ReceivedSessionBytes;
                    if (frame.HasPixelContent)
                    {
                        if (!client.BootstrapState.IsFirstPixelConfirmed)
                            client.ConfirmBootstrap(frame.Size, checked((int)frame.Statistics.TransferStatistics.RectangleCount));
                        frames++;
                        if (sizes.Add(frame.Size)) Console.WriteLine($"Stage={stage} PixelSize={frame.Size.Width}x{frame.Size.Height}");
                    }
                    break;
                }
                finally { (message as IDisposable)?.Dispose(); }
            }
        }
        Console.WriteLine(FormattableString.Invariant($"Stage={stage} FullPixelFrames={frames} Seconds={timer.Elapsed.TotalSeconds:F3} FPS={frames / timer.Elapsed.TotalSeconds:F2} MiBps={bytes / timer.Elapsed.TotalSeconds / 1048576:F3}"));
        return sizes;
    }
}
