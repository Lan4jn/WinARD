using WinARD.Application.Ports;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class RemoteSessionViewModelTests
{
    [Fact]
    public async Task Dispose_cancels_single_receive_loop_and_releases_resources_once()
    {
        var runtime = new BlockingRuntime();
        var lifetime = new TrackingLifetime();
        var presenter = new TrackingPresenter();
        var viewModel = new RemoteSessionViewModel(
            runtime,
            lifetime,
            presenter,
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.ReceiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(viewModel.DisposeAsync().AsTask(), viewModel.DisposeAsync().AsTask());

        Assert.Equal([false], runtime.UpdateRequests);
        Assert.Equal(1, runtime.MaximumConcurrentReceives);
        Assert.True(runtime.ReceiveCancelled);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Equal(1, presenter.DisposeCount);
    }

    [Fact]
    public async Task Network_failure_is_observed_sanitized_and_releases_ownership()
    {
        var runtime = new FailingRuntime();
        var lifetime = new TrackingLifetime();
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            lifetime,
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("连接已中断。", viewModel.StatusMessage);
        Assert.DoesNotContain("sensitive", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Dispose_releases_presenter_through_ui_dispatcher()
    {
        var dispatcher = new TrackingDispatcher();
        var presenter = new DispatcherBoundPresenter(() => dispatcher.IsDispatching);
        var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new TrackingLifetime(),
            presenter,
            dispatcher,
            clipboardBridge: null);

        await viewModel.DisposeAsync();

        Assert.True(presenter.WasDisposed);
    }

    [Fact]
    public async Task Dispose_releases_ownership_when_presenter_cleanup_fails()
    {
        var lifetime = new TrackingLifetime();
        var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            lifetime,
            new ThrowingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        _ = await Assert.ThrowsAsync<AggregateException>(() => viewModel.DisposeAsync().AsTask());

        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Incremental_update_is_requested_only_after_framebuffer_messages()
    {
        var runtime = new ScriptedRuntime(
            new RemoteClipboardMessage("clipboard"),
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(1, 1),
                [0, 0, 0, 255],
                4,
                [new RemoteRectangle(0, 0, 1, 1)]));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.DisposeAsync();

        Assert.Equal([(false, 0), (true, 2)], runtime.UpdateRequests);
    }

    [Fact]
    public async Task Framebuffer_size_tracks_the_latest_frame()
    {
        var runtime = new ScriptedRuntime(
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(2, 3),
                new byte[24],
                8,
                [new RemoteRectangle(0, 0, 2, 3)]));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new RemoteFramebufferSize(2, 3), viewModel.FramebufferSize);
    }

    private sealed class BlockingRuntime : IRemoteSessionRuntime
    {
        private int _receives;
        private int _activeReceives;
        public TaskCompletionSource ReceiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<bool> UpdateRequests { get; } = [];
        public int MaximumConcurrentReceives { get; private set; }
        public bool ReceiveCancelled { get; private set; }
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            UpdateRequests.Add(incremental);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _receives);
            var active = Interlocked.Increment(ref _activeReceives);
            MaximumConcurrentReceives = Math.Max(MaximumConcurrentReceives, active);
            ReceiveEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }
            catch (OperationCanceledException)
            {
                ReceiveCancelled = true;
                throw;
            }
            finally
            {
                _ = Interlocked.Decrement(ref _activeReceives);
            }
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingRuntime : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<RemoteServerMessage>(new IOException("sensitive endpoint"));
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedRuntime(params RemoteServerMessage[] messages) : IRemoteSessionRuntime
    {
        private readonly Queue<RemoteServerMessage> _messages = new(messages);
        private int _receiveCount;
        public TaskCompletionSource MessagesConsumed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(bool Incremental, int ReceiveCount)> UpdateRequests { get; } = [];
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            UpdateRequests.Add((incremental, _receiveCount));
            return ValueTask.CompletedTask;
        }

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_messages.TryDequeue(out var message))
            {
                _receiveCount++;
                if (_messages.Count == 0)
                {
                    MessagesConsumed.TrySetResult();
                }

                return message;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingLifetime : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class TrackingPresenter : IFramePresenter
    {
        public int DisposeCount { get; private set; }
        public void Resize(int width, int height) { }
        public void Present(ReadOnlySpan<byte> bgra32, int stride, IReadOnlyList<RemoteRectangle> dirtyRectangles) { }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class ThrowingPresenter : IFramePresenter
    {
        public void Resize(int width, int height) { }
        public void Present(ReadOnlySpan<byte> bgra32, int stride, IReadOnlyList<RemoteRectangle> dirtyRectangles) { }
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("presenter cleanup failed"));
    }

    private sealed class DispatcherBoundPresenter(Func<bool> isDispatching) : IFramePresenter
    {
        public bool WasDisposed { get; private set; }
        public void Resize(int width, int height) { }
        public void Present(ReadOnlySpan<byte> bgra32, int stride, IReadOnlyList<RemoteRectangle> dirtyRectangles) { }

        public ValueTask DisposeAsync()
        {
            if (!isDispatching())
            {
                throw new InvalidOperationException("Presenter disposal must run through the UI dispatcher.");
            }

            WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingDispatcher : IUiDispatcher
    {
        public bool IsDispatching { get; private set; }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsDispatching = true;
            try
            {
                action();
            }
            finally
            {
                IsDispatching = false;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
