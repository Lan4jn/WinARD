namespace WinARD.Application.Ports;

public readonly record struct RemoteFramebufferSize(int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

public readonly record struct RemotePoint(int X, int Y);

public readonly record struct RemoteRectangle(int X, int Y, int Width, int Height);

[Flags]
public enum RemotePointerButtons
{
    None = 0,
    Left = 1,
    Middle = 2,
    Right = 4,
}

public abstract record RemoteServerMessage;

public sealed record RemoteFramebufferMessage(
    RemoteFramebufferSize Size,
    byte[] Bgra32,
    int Stride,
    IReadOnlyList<RemoteRectangle> DirtyRectangles) : RemoteServerMessage;

public sealed record RemoteClipboardMessage(string Text) : RemoteServerMessage;

public sealed record RemoteBellMessage : RemoteServerMessage;

public interface IRemoteSessionRuntime
{
    RemoteFramebufferSize FramebufferSize { get; }

    ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken);

    ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken);

    ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken);

    ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken);

    ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken);

    ValueTask DisconnectAsync();
}
