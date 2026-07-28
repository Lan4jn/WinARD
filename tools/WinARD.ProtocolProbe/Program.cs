using WinARD.ProtocolProbe;
using WinARD.Remote.Protocol.Authentication;

internal static class Program
{
    private const int DefaultPort = 5900;

    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            if (!ProbeCommandLine.TryParse(args, out var request))
            {
                PrintUsage();
                return 2;
            }

            var host = ReadRequiredText("WINARD_HOST", "Host: ");
            var usernameText = ReadRequiredText("WINARD_USERNAME", "Username: ");
            if (host is null || usernameText is null || !TryReadPort(out var port))
            {
                PrintUsage();
                return 2;
            }

            using var username = SecretMaterial.FromUtf8(usernameText);
            using var password = HiddenPasswordReader.Read(new SystemPasswordConsole(), cancellation.Token);
            if (password is null)
            {
                PrintUsage();
                return 2;
            }

            var result = await new ProbeRunner().RunAsync(
                host,
                port,
                username,
                password,
                request,
                cancellation.Token);

            Console.WriteLine($"Version: {result.Version.Major}.{result.Version.Minor}");
            Console.WriteLine($"Security: {result.SecurityType} ({(byte)result.SecurityType})");
            Console.WriteLine("Authentication: success");
            if (result.Capture is { } capture)
            {
                Console.WriteLine(ProbeOutput.FormatCapture(capture));
            }

            if (result.PointerSmoke is { } pointerSmoke)
            {
                Console.WriteLine(ProbeOutput.FormatPointerSmoke(pointerSmoke));
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(ProbeOutput.FormatFailure(exception));
            return 1;
        }
    }

    private static string? ReadRequiredText(string environmentVariable, string prompt)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        if (Console.IsInputRedirected)
        {
            return null;
        }

        Console.Write(prompt);
        value = Console.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TryReadPort(out int port)
    {
        var value = Environment.GetEnvironmentVariable("WINARD_PORT");
        if (string.IsNullOrWhiteSpace(value) && !Console.IsInputRedirected)
        {
            Console.Write($"Port [{DefaultPort}]: ");
            value = Console.ReadLine();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            port = DefaultPort;
            return true;
        }

        return int.TryParse(value, out port) && port is >= 1 and <= ushort.MaxValue;
    }

    private static void PrintUsage() =>
        Console.Error.WriteLine(
            "Usage: WinARD.ProtocolProbe [--capture-first-frame <path.bgra|path.bmp> | --pointer-smoke]. Set WINARD_HOST and WINARD_USERNAME (optional WINARD_PORT, default 5900), then run from an interactive console so the password can be read without echo.");
}
