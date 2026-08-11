using System.Globalization;
using WinARD.ProtocolProbe;
using WinARD.ProtocolProbe.EncodingResearch;
using WinARD.ProtocolProbe.RdmCapture;
using WinARD.Remote.Protocol.Authentication;

internal static class Program
{
    private const int DefaultPort = 5900;
    private const int DefaultRdmListenPort = 5901;

    internal static string UsageText =>
        "Usage: WinARD.ProtocolProbe [--capture-first-frame <path.bgra|path.bmp> | --pointer-smoke]. Set WINARD_HOST and WINARD_USERNAME (optional WINARD_PORT, default 5900), then run from an interactive console so the password can be read without echo."
        + Environment.NewLine
        + "Research commands:"
        + Environment.NewLine
        + @"  & '.\WinARD.ProtocolProbe.exe' --listen-rdm adaptive-default '.\artifacts\protocol-research\rdm\adaptive-default.json'"
        + " (optional WINARD_RDM_IDLE_TIMEOUT_SECONDS, 1..600, default 10)"
        + Environment.NewLine
        + @"  & '.\WinARD.ProtocolProbe.exe' --compare-rdm-captures '.\artifacts\protocol-research\rdm\full.json' '.\artifacts\protocol-research\rdm\adaptive-default.json'"
        + Environment.NewLine
        + @"  & '.\WinARD.ProtocolProbe.exe' --capture-known-encoding-prefix 1002 '.\artifacts\protocol-research\mac\apple-1002' --confirm-synthetic-screen";

    internal static bool TryParseRdmListenPort(string? value, out int port)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            port = DefaultRdmListenPort;
            return true;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort)
            && parsedPort is >= 1 and <= ushort.MaxValue)
        {
            port = parsedPort;
            return true;
        }

        port = 0;
        return false;
    }

    internal static bool TryParseRdmIdleTimeout(string? value, out TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            timeout = RdmCaptureServer.DefaultInactivityTimeout;
            return true;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds is >= 1 and <= 600)
        {
            timeout = TimeSpan.FromSeconds(seconds);
            return true;
        }

        timeout = TimeSpan.Zero;
        return false;
    }

    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        return await RunAsync(
            args,
            Environment.GetEnvironmentVariable,
            Console.Out,
            Console.Error,
            cancellation.Token).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        string[] args,
        Func<string, string?> readEnvironmentVariable,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        Func<TimeSpan, int, string, CancellationToken, Task<RdmCaptureReport>>? captureRdm = null,
        EncodingPrefixCaptureOperation? captureEncodingPrefix = null,
        Func<CancellationToken, ISecretMaterial?>? readPassword = null)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            if (!ProbeCommandLine.TryParse(args, out var request))
            {
                PrintUsage(error);
                return 2;
            }

            if (request.Mode == ProbeMode.ListenRdm)
            {
                if (!TryParseRdmListenPort(
                        readEnvironmentVariable("WINARD_LISTEN_PORT"),
                        out var listenPort)
                    || !TryParseRdmIdleTimeout(
                        readEnvironmentVariable("WINARD_RDM_IDLE_TIMEOUT_SECONDS"),
                        out var idleTimeout))
                {
                    PrintUsage(error);
                    return 2;
                }

                captureRdm ??= static (timeout, port, profile, token) =>
                    new RdmCaptureServer(timeout).CaptureOnceAsync(port, profile, token);
                return await RunRdmListenerAsync(
                    request,
                    listenPort,
                    idleTimeout,
                    captureRdm,
                    output,
                    cancellationToken).ConfigureAwait(false);
            }

            if (request.Mode == ProbeMode.CompareRdmCaptures)
            {
                return await RunRdmComparisonAsync(request, output, cancellationToken)
                    .ConfigureAwait(false);
            }

            var host = ReadRequiredText(
                "WINARD_HOST",
                "Host: ",
                readEnvironmentVariable,
                output);
            var usernameText = ReadRequiredText(
                "WINARD_USERNAME",
                "Username: ",
                readEnvironmentVariable,
                output);
            if (host is null
                || usernameText is null
                || !TryReadPort(readEnvironmentVariable, output, out var port))
            {
                PrintUsage(error);
                return 2;
            }

            using var username = SecretMaterial.FromUtf8(usernameText);
            readPassword ??= token => HiddenPasswordReader.Read(new SystemPasswordConsole(), token);
            using var password = readPassword(cancellationToken);
            if (password is null)
            {
                PrintUsage(error);
                return 2;
            }

            if (request.Mode == ProbeMode.CaptureKnownEncodingPrefix)
            {
                captureEncodingPrefix ??= new EncodingPrefixCaptureRunner().RunAsync;
                var prefixCaptures = await captureEncodingPrefix(
                    host,
                    port,
                    username,
                    password,
                    request.CandidateEncodingId!.Value,
                    request.OutputPath!,
                    request.SyntheticScreenConfirmed,
                    cancellationToken).ConfigureAwait(false);
                foreach (var prefixCapture in prefixCaptures)
                {
                    output.WriteLine(ProbeOutput.FormatEncodingPrefixCaptured(prefixCapture));
                }
                return 0;
            }

            var result = await new ProbeRunner().RunRequestAsync(
                host,
                port,
                username,
                password,
                request,
                cancellationToken).ConfigureAwait(false);

            output.WriteLine($"Version: {result.Version.Major}.{result.Version.Minor}");
            output.WriteLine($"Security: {result.SecurityType} ({(byte)result.SecurityType})");
            output.WriteLine("Authentication: success");
            if (result.Capture is { } capture)
            {
                output.WriteLine(ProbeOutput.FormatCapture(capture));
            }

            if (result.PointerSmoke is { } pointerSmoke)
            {
                output.WriteLine(ProbeOutput.FormatPointerSmoke(pointerSmoke));
            }

            return 0;
        }
        catch (Exception exception)
        {
            error.WriteLine(ProbeOutput.FormatFailure(exception));
            return 1;
        }
    }

    private static async Task<int> RunRdmListenerAsync(
        ProbeRequest request,
        int port,
        TimeSpan idleTimeout,
        Func<TimeSpan, int, string, CancellationToken, Task<RdmCaptureReport>> captureRdm,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        output.WriteLine(ProbeOutput.FormatRdmListening(port, request.ProfileName!));
        var report = await captureRdm(idleTimeout, port, request.ProfileName!, cancellationToken)
            .ConfigureAwait(false);
        await RdmCaptureFile.WriteAsync(request.OutputPath!, report, cancellationToken)
            .ConfigureAwait(false);
        output.WriteLine(ProbeOutput.FormatRdmSaved(request.OutputPath!));
        return 0;
    }

    private static async Task<int> RunRdmComparisonAsync(
        ProbeRequest request,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var baseline = await RdmCaptureFile.ReadAsync(
            request.BaselineCapturePath!,
            cancellationToken).ConfigureAwait(false);
        var adaptive = await RdmCaptureFile.ReadAsync(
            request.AdaptiveCapturePath!,
            cancellationToken).ConfigureAwait(false);
        var candidate = RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(baseline, adaptive);
        output.WriteLine(ProbeOutput.FormatRdmComparisonCandidate(candidate));
        return 0;
    }

    private static string? ReadRequiredText(
        string environmentVariable,
        string prompt,
        Func<string, string?> readEnvironmentVariable,
        TextWriter output)
    {
        var value = readEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        if (Console.IsInputRedirected)
        {
            return null;
        }

        output.Write(prompt);
        value = Console.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TryReadPort(
        Func<string, string?> readEnvironmentVariable,
        TextWriter output,
        out int port)
    {
        var value = readEnvironmentVariable("WINARD_PORT");
        if (string.IsNullOrWhiteSpace(value) && !Console.IsInputRedirected)
        {
            output.Write($"Port [{DefaultPort}]: ");
            value = Console.ReadLine();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            port = DefaultPort;
            return true;
        }

        return int.TryParse(value, out port) && port is >= 1 and <= ushort.MaxValue;
    }

    private static void PrintUsage(TextWriter error) => error.WriteLine(UsageText);
}
