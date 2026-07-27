using WinARD.Application.Ports;
using WinARD.Desktop.Input;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Services;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using Xunit;

namespace WinARD.Desktop.Tests.Services;

public sealed class RemoteSessionWindowLifecycleTests
{
    [Fact]
    public async Task TerminalRuntimeFailureKeepsErrorWindowInteractiveUntilOneRetryClosesAndReconnects()
    {
        var ownership = new TrackingOwnership();
        var viewModel = new RemoteSessionViewModel(
            new FailingRuntime(),
            ownership,
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);
        var sequence = new List<string>();
        var stopCalls = 0;
        var windowCloseCalls = 0;
        var connectCalls = 0;
        var openCalls = 0;
        var lifecycle = new RemoteSessionWindowLifecycle(
            viewModel.Completion,
            () => viewModel.Error is not null,
            async () =>
            {
                sequence.Add("stop-start");
                stopCalls++;
                await viewModel.DisposeAsync();
                sequence.Add("stop-complete");
            },
            () =>
            {
                sequence.Add("close-window");
                windowCloseCalls++;
                return Task.CompletedTask;
            },
            _ =>
            {
                sequence.Add("connect");
                connectCalls++;
                sequence.Add("open");
                openCalls++;
                return Task.CompletedTask;
            });

        var observation = lifecycle.ObserveCompletionAsync(CancellationToken.None);
        await viewModel.StartAsync(CancellationToken.None);
        await observation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("REMOTE_SESSION_INTERRUPTED", viewModel.Error?.Code);
        Assert.Equal(1, ownership.DisposeCount);
        Assert.Equal(1, stopCalls);
        Assert.Equal(0, windowCloseCalls);
        Assert.True(lifecycle.CanRetry);

        await Task.WhenAll(
            lifecycle.RetryAsync(CancellationToken.None),
            lifecycle.RetryAsync(CancellationToken.None));

        Assert.Equal(
            ["stop-start", "stop-complete", "close-window", "connect", "open"],
            sequence);
        Assert.Equal(1, stopCalls);
        Assert.Equal(1, windowCloseCalls);
        Assert.Equal(1, ownership.DisposeCount);
        Assert.Equal(1, connectCalls);
        Assert.Equal(1, openCalls);
    }

    [Fact]
    public async Task InputFailureStopsSessionButKeepsErrorActionsAvailableUntilDisconnect()
    {
        var ownership = new TrackingOwnership();
        var viewModel = new RemoteSessionViewModel(
            new FailingInputRuntime(),
            ownership,
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);
        var windowCloseCalls = 0;
        var exports = 0;
        var lifecycle = new RemoteSessionWindowLifecycle(
            viewModel.Completion,
            () => viewModel.Error is not null,
            () => viewModel.DisposeAsync().AsTask(),
            () =>
            {
                windowCloseCalls++;
                return Task.CompletedTask;
            },
            retryRequested: null);
        var runner = new RemoteInputOperationRunner(
            new InlineDispatcher(),
            viewModel.ReportInputFailureAsync,
            lifecycle.StopSessionAsync,
            () => lifecycle.IsSessionStopped,
            viewModel.ObserveInputFailure);
        var actions = new ConnectionErrorActionHandler(
        [
            new(ConnectionErrorActionKind.ExportDiagnostics, _ =>
            {
                exports++;
                return Task.CompletedTask;
            }),
            new(ConnectionErrorActionKind.Disconnect, _ => lifecycle.DisconnectAsync()),
        ]);

        await runner.RunAsync(
            () => viewModel.SendPointerAsync(
                buttons: 0,
                new RemotePoint(0, 0),
                CancellationToken.None).AsTask());

        Assert.Equal("REMOTE_INPUT_FAILED", viewModel.Error?.Code);
        Assert.Equal(1, ownership.DisposeCount);
        Assert.True(lifecycle.IsSessionStopped);
        Assert.Equal(0, windowCloseCalls);
        Assert.True(actions.CanHandle(ConnectionErrorActionKind.ExportDiagnostics));
        Assert.True(actions.CanHandle(ConnectionErrorActionKind.Disconnect));

        await actions.HandleAsync(
            ConnectionErrorActionKind.ExportDiagnostics,
            CancellationToken.None);
        await actions.HandleAsync(
            ConnectionErrorActionKind.Disconnect,
            CancellationToken.None);

        Assert.Equal(1, exports);
        Assert.Equal(1, windowCloseCalls);
    }

    [Fact]
    public async Task DisconnectWhileRetryIsWaitingPreventsReconnectAndIsIdempotent()
    {
        var retryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRetry = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopCalls = 0;
        var windowCloseCalls = 0;
        var reconnectCalls = 0;
        var lifecycle = new RemoteSessionWindowLifecycle(
            Task.CompletedTask,
            () => true,
            () =>
            {
                stopCalls++;
                return Task.CompletedTask;
            },
            () =>
            {
                windowCloseCalls++;
                return Task.CompletedTask;
            },
            async cancellationToken =>
            {
                retryEntered.TrySetResult();
                await allowRetry.Task;
                cancellationToken.ThrowIfCancellationRequested();
                reconnectCalls++;
            });

        var retry = lifecycle.RetryAsync(CancellationToken.None);
        await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(lifecycle.CanRetry);
        Assert.True(lifecycle.CanDisconnect);
        var firstDisconnect = lifecycle.DisconnectAsync();
        var secondDisconnect = lifecycle.DisconnectAsync();
        allowRetry.TrySetResult();

        await Task.WhenAll(firstDisconnect, secondDisconnect);
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);

        Assert.Equal(1, stopCalls);
        Assert.Equal(1, windowCloseCalls);
        Assert.Equal(0, reconnectCalls);
        Assert.False(lifecycle.CanRetry);
        Assert.False(lifecycle.CanDisconnect);
    }

    private sealed class FailingRuntime : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(
            bool incremental,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<RemoteServerMessage>(
                new IOException("sensitive endpoint"));

        public ValueTask SendPointerAsync(
            byte buttons,
            int x,
            int y,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask SendKeyAsync(
            uint keysym,
            bool down,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask SendClipboardTextAsync(
            string text,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingInputRuntime : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(
            bool incremental,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }

        public ValueTask SendPointerAsync(
            byte buttons,
            int x,
            int y,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("sensitive input failure"));

        public ValueTask SendKeyAsync(
            uint keysym,
            bool down,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask SendClipboardTextAsync(
            string text,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingOwnership : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingPresenter : IFramePresenter
    {
        public void Resize(int width, int height)
        {
        }

        public void Present(
            ReadOnlySpan<byte> bgra32,
            int stride,
            IReadOnlyList<RemoteRectangle> dirtyRectangles)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
