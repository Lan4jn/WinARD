using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Authentication;

namespace WinARD.ProtocolProbe;

internal interface IPasswordConsole
{
    bool IsInputRedirected { get; }

    bool TreatControlCAsInput { get; set; }

    ConsoleKeyInfo ReadKey(bool intercept);

    void Write(string value);

    void WriteLine();
}

internal static class HiddenPasswordReader
{
    private static readonly AsyncLocal<Action<char[], SecretMaterial?>?> CleanupObserverStorage = new();

    internal static Action<char[], SecretMaterial?>? CleanupObserver
    {
        get => CleanupObserverStorage.Value;
        set => CleanupObserverStorage.Value = value;
    }

    public static SecretMaterial? Read(IPasswordConsole console, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        if (console.IsInputRedirected)
        {
            return null;
        }

        var previousTreatControlCAsInput = console.TreatControlCAsInput;
        var characters = new char[256];
        var length = 0;
        SecretMaterial? result = null;
        ExceptionDispatchInfo? pendingException = null;
        console.Write("Password: ");
        try
        {
            console.TreatControlCAsInput = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    console.WriteLine();
                    result = SecretMaterial.FromUtf8(characters.AsSpan(0, length));
                    break;
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
        catch (Exception exception)
        {
            pendingException = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
            try
            {
                console.TreatControlCAsInput = previousTreatControlCAsInput;
            }
            catch (Exception exception)
            {
                result?.Dispose();
                pendingException = ExceptionDispatchInfo.Capture(exception);
            }

            CleanupObserver?.Invoke(characters, result);
        }

        pendingException?.Throw();
        return result;
    }
}

internal sealed class SystemPasswordConsole : IPasswordConsole
{
    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool TreatControlCAsInput
    {
        get => Console.TreatControlCAsInput;
        set => Console.TreatControlCAsInput = value;
    }

    public ConsoleKeyInfo ReadKey(bool intercept) => Console.ReadKey(intercept);

    public void Write(string value) => Console.Write(value);

    public void WriteLine() => Console.WriteLine();
}
