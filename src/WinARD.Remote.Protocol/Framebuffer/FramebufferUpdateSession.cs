using WinARD.Remote.Protocol.Encodings;
using WinARD.Remote.Protocol.Errors;
using System.Runtime.ExceptionServices;

namespace WinARD.Remote.Protocol.Framebuffer;

public sealed class FramebufferUpdateSession : IAsyncDisposable
{
    private readonly Framebuffer _framebuffer;
    private readonly IReadOnlyDictionary<int, IRfbEncodingDecoder> _decoders;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private SessionState _state = SessionState.Active;
    private Task? _disposeTask;

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
        ThrowIfUnavailable();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            try
            {
                return await FramebufferUpdateReader.ApplyAsync(
                    stream,
                    _framebuffer,
                    _decoders,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_stateLock)
                {
                    if (_state == SessionState.Active)
                    {
                        _state = SessionState.Faulted;
                    }
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FramebufferUpdateResult> ApplyBodyAsync(
        Stream stream,
        CancellationToken cancellationToken)
        => await ApplyBodyAsync(
            stream,
            static (_, result) => result,
            cancellationToken).ConfigureAwait(false);

    public async Task<TResult> ApplyBodyAsync<TResult>(
        Stream stream,
        Func<Framebuffer, FramebufferUpdateResult, TResult> projector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(projector);
        ThrowIfUnavailable();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            try
            {
                var result = await FramebufferUpdateReader.ApplyBodyAsync(
                    stream,
                    _framebuffer,
                    _decoders,
                    cancellationToken).ConfigureAwait(false);
                return projector(_framebuffer, result);
            }
            catch
            {
                lock (_stateLock)
                {
                    if (_state == SessionState.Active)
                    {
                        _state = SessionState.Faulted;
                    }
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReconfigurePixelFormatAsync(
        PixelFormat pixelFormat,
        CancellationToken cancellationToken)
    {
        await using var reconfiguration = await PreparePixelFormatReconfigurationAsync(
            pixelFormat,
            cancellationToken).ConfigureAwait(false);
        reconfiguration.Commit();
    }

    public async Task<FramebufferPixelFormatReconfiguration> PreparePixelFormatReconfigurationAsync(
        PixelFormat pixelFormat,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pixelFormat);
        ThrowIfUnavailable();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var reconfigurableDecoders = _decoders.Values
                .OfType<IReconfigurablePixelFormatDecoder>()
                .Distinct<IReconfigurablePixelFormatDecoder>(ReferenceEqualityComparer.Instance)
                .ToArray();
            foreach (var decoder in reconfigurableDecoders)
            {
                decoder.ValidatePixelFormat(pixelFormat);
            }

            return new FramebufferPixelFormatReconfiguration(
                this,
                pixelFormat,
                reconfigurableDecoders);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    internal void CommitPixelFormatReconfiguration(
        PixelFormat pixelFormat,
        IReadOnlyList<IReconfigurablePixelFormatDecoder> decoders)
    {
        ThrowIfUnavailable();
        try
        {
            foreach (var decoder in decoders)
            {
                decoder.CommitPixelFormat(pixelFormat);
            }
        }
        catch
        {
            lock (_stateLock)
            {
                if (_state == SessionState.Active)
                {
                    _state = SessionState.Faulted;
                }
            }

            throw;
        }
    }

    internal void ReleasePixelFormatReconfiguration() => _gate.Release();

    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposeTask is null)
            {
                _state = SessionState.Disposing;
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        List<Exception>? failures = null;
        try
        {
            foreach (var decoder in _decoders.Values.Distinct(ReferenceEqualityComparer.Instance))
            {
                try
                {
                    if (decoder is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    }
                    else if (decoder is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            lock (_stateLock)
            {
                _state = failures is null ? SessionState.Disposed : SessionState.Faulted;
            }

            if (failures is { Count: 1 })
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures is not null)
            {
                throw new AggregateException(failures);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfUnavailable()
    {
        lock (_stateLock)
        {
            switch (_state)
            {
                case SessionState.Active:
                    return;
                case SessionState.Faulted:
                    throw new RfbProtocolException(
                        "The framebuffer update session is faulted; discard it and the connection.");
                case SessionState.Disposing:
                case SessionState.Disposed:
                    throw new ObjectDisposedException(nameof(FramebufferUpdateSession));
                default:
                    throw new InvalidOperationException("Unknown framebuffer update session state.");
            }
        }
    }

    private enum SessionState
    {
        Active,
        Faulted,
        Disposing,
        Disposed,
    }
}

public sealed class FramebufferPixelFormatReconfiguration : IAsyncDisposable
{
    private readonly FramebufferUpdateSession _session;
    private readonly PixelFormat _pixelFormat;
    private readonly IReadOnlyList<IReconfigurablePixelFormatDecoder> _decoders;
    private readonly object _stateLock = new();
    private bool _completed;

    internal FramebufferPixelFormatReconfiguration(
        FramebufferUpdateSession session,
        PixelFormat pixelFormat,
        IReadOnlyList<IReconfigurablePixelFormatDecoder> decoders)
    {
        _session = session;
        _pixelFormat = pixelFormat;
        _decoders = decoders;
    }

    public void Commit()
    {
        lock (_stateLock)
        {
            if (_completed)
            {
                throw new InvalidOperationException("The pixel format reconfiguration is no longer pending.");
            }

            try
            {
                _session.CommitPixelFormatReconfiguration(_pixelFormat, _decoders);
            }
            finally
            {
                _completed = true;
                _session.ReleasePixelFormatReconfiguration();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (!_completed)
            {
                _completed = true;
                _session.ReleasePixelFormatReconfiguration();
            }
        }

        return ValueTask.CompletedTask;
    }
}
