using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Handshake;

internal static class Program
{
    private const int DefaultPort = 5900;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static async Task<int> Main()
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var host = ReadRequiredText("WINARD_HOST", "Host: ");
            var usernameText = ReadRequiredText("WINARD_USERNAME", "Username: ");
            if (host is null || usernameText is null || !TryReadPort(out var port))
            {
                PrintUsage();
                return 2;
            }

            using var username = SecretMaterial.FromUtf8(usernameText);
            using var password = ReadHiddenPassword(cancellation.Token);
            if (password is null)
            {
                PrintUsage();
                return 2;
            }

            using var client = new TcpClient();
            using (var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
            {
                connectCancellation.CancelAfter(ConnectTimeout);
                try
                {
                    await client.ConnectAsync(host, port, connectCancellation.Token);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    Console.Error.WriteLine("Connection timed out.");
                    return 1;
                }
            }

            await using var stream = client.GetStream();
            var handshake = await RfbHandshake.NegotiateAsync(stream, cancellation.Token);
            if (handshake.SecurityType != RfbSecurityType.AppleRemoteDesktop)
            {
                throw new RfbProtocolException("The server did not negotiate Apple Remote Desktop security type 30.");
            }

            await new ArdAuthenticator().AuthenticateAsync(
                stream,
                handshake.Version,
                username,
                password,
                cancellation.Token);

            Console.WriteLine($"Version: {handshake.Version.Major}.{handshake.Version.Minor}");
            Console.WriteLine($"Security: {handshake.SecurityType} ({(byte)handshake.SecurityType})");
            Console.WriteLine("Authentication: success");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Probe failed: {exception.Message}");
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

    private static SecretMaterial? ReadHiddenPassword(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            return null;
        }

        var characters = new char[256];
        var length = 0;
        Console.Write("Password: ");
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return SecretMaterial.FromUtf8(characters.AsSpan(0, length));
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (length > 0)
                    {
                        characters[--length] = '\0';
                    }

                    continue;
                }

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (!char.IsControl(key.KeyChar))
                {
                    if (length == characters.Length)
                    {
                        throw new ArgumentException("Password input is too long.");
                    }

                    characters[length++] = key.KeyChar;
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }

    private static void PrintUsage() =>
        Console.Error.WriteLine(
            "Set WINARD_HOST and WINARD_USERNAME (optional WINARD_PORT, default 5900), then run from an interactive console so the password can be read without echo.");
}
