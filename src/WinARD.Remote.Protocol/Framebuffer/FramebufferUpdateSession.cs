using WinARD.Remote.Protocol.Encodings;

namespace WinARD.Remote.Protocol.Framebuffer;

public sealed class FramebufferUpdateSession : IDisposable
{
    private readonly Framebuffer _framebuffer;
    private readonly IReadOnlyDictionary<int, IRfbEncodingDecoder> _decoders;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    internal FramebufferUpdateSession(
        Framebuffer framebuffer,
        IReadOnlyDictionary<int, IRfbEncodingDecoder> decoders)
    {
        ArgumentNullException.ThrowIfNull(framebuffer);
        ArgumentNullException.ThrowIfNull(decoders);
        _framebuffer = framebuffer;
        _decoders = decoders;
    }

    public async Task<FramebufferUpdateResult> ApplyAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await FramebufferUpdateReader.ApplyAsync(
                stream,
                _framebuffer,
                _decoders,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            foreach (var decoder in _decoders.Values.OfType<IDisposable>())
            {
                decoder.Dispose();
            }

            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
