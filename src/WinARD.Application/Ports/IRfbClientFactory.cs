namespace WinARD.Application.Ports;

public interface IRfbClientFactory
{
    IRfbClient Create(Stream stream);
}

public interface IRfbClient : IAsyncDisposable
{
    RemoteFramebufferSize FramebufferSize => default;

    RemoteDisplayCapabilities DisplayCapabilities => RemoteDisplayCapabilities.Unknown;

    RemoteRuntimePerformanceSnapshot PerformanceSnapshot =>
        RemoteRuntimePerformanceSnapshot.Empty;

    /// <summary>
    /// Idempotently stops accepting new runtime writes and aborts any active scheduled write.
    /// Implementations without background or scheduled writes may keep the default no-op behavior.
    /// This method must not dispose protocol decoders or the underlying transport.
    /// </summary>
    void BeginShutdown()
    {
    }

    Task NegotiateAsync(CancellationToken cancellationToken);

    Task AuthenticateAsync(
        string username,
        ISecret secret,
        CancellationToken cancellationToken);

    Task InitializeAsync(CancellationToken cancellationToken);

    ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("Runtime framebuffer updates are not supported."));

    ValueTask<QualityTransitionStatus> ApplyQualityTransitionAsync(
        RemoteQualitySettings settings,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(QualityTransitionStatus.CapabilityUnavailable);

    ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<RemoteServerMessage>(
            new NotSupportedException("Runtime server messages are not supported."));

    ValueTask SendPointerAsync(
        byte buttons,
        int x,
        int y,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("Runtime pointer input is not supported."));

    ValueTask SendKeyAsync(
        uint keysym,
        bool down,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("Runtime keyboard input is not supported."));

    ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("Runtime clipboard is not supported."));
}
