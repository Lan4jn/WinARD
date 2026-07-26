using WinARD.Desktop.Clipboard;
using WinARD.Desktop.Threading;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests;

public sealed class ClipboardBridgeTests
{
    [Fact]
    public async Task Remote_update_sets_sanitized_local_text_without_looping_back()
    {
        var adapter = new TestClipboardAdapter();
        var sent = new List<string>();
        await using var bridge = new WindowsClipboardBridge(
            adapter,
            new InlineDispatcher(),
            (text, _) => { sent.Add(text); return ValueTask.CompletedTask; },
            maxUtf8Bytes: 32);

        await bridge.SetRemoteTextAsync("remote\0text", CancellationToken.None);
        await bridge.WhenIdleAsync();

        Assert.Equal("remotetext", adapter.Text);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Local_change_is_sent_once_and_oversized_text_is_ignored()
    {
        var adapter = new TestClipboardAdapter();
        var sent = new List<string>();
        await using var bridge = new WindowsClipboardBridge(
            adapter,
            new InlineDispatcher(),
            (text, _) => { sent.Add(text); return ValueTask.CompletedTask; },
            maxUtf8Bytes: 5);

        adapter.SetExternalText("hello");
        await bridge.WhenIdleAsync();
        adapter.SetExternalText("toolong");
        await bridge.WhenIdleAsync();

        Assert.Equal(["hello"], sent);
    }

    [Fact]
    public async Task Dispose_unsubscribes_observer()
    {
        var adapter = new TestClipboardAdapter();
        var sent = new List<string>();
        var bridge = new WindowsClipboardBridge(
            adapter,
            new InlineDispatcher(),
            (text, _) => { sent.Add(text); return ValueTask.CompletedTask; });

        await bridge.DisposeAsync();
        adapter.SetExternalText("after");

        Assert.Equal(0, adapter.SubscriptionCount);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Dispose_unsubscribes_observer_when_bridge_is_disabled()
    {
        var adapter = new TestClipboardAdapter();
        var bridge = new WindowsClipboardBridge(
            adapter,
            new InlineDispatcher(),
            (_, _) => ValueTask.CompletedTask);
        bridge.IsEnabled = false;

        await bridge.DisposeAsync();

        Assert.Equal(0, adapter.SubscriptionCount);
    }

    [Fact]
    public async Task Adapter_access_and_subscription_lifecycle_are_ui_affine()
    {
        await using var dispatcher = new DedicatedThreadDispatcher();
        var adapter = new ThreadRecordingClipboardAdapter();
        var bridge = new WindowsClipboardBridge(
            adapter,
            dispatcher,
            (_, _) => ValueTask.CompletedTask);

        await bridge.SetRemoteTextAsync("remote", CancellationToken.None);
        await bridge.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        adapter.SetExternalText("local");
        await bridge.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Run(async () => await bridge.DisposeAsync());

        Assert.Equal(dispatcher.ThreadId, adapter.SubscriptionThreadId);
        Assert.Equal(dispatcher.ThreadId, adapter.UnsubscriptionThreadId);
        Assert.All(adapter.ReadThreadIds, threadId => Assert.Equal(dispatcher.ThreadId, threadId));
        Assert.All(adapter.WriteThreadIds, threadId => Assert.Equal(dispatcher.ThreadId, threadId));
        Assert.NotEmpty(adapter.ReadThreadIds);
        Assert.NotEmpty(adapter.WriteThreadIds);
    }

    [Fact]
    public void Constructor_propagates_dispatcher_subscription_failure_without_touching_adapter()
    {
        var adapter = new TestClipboardAdapter();
        var dispatcher = new SwitchableDispatcher { Fail = true };

        var exception = Assert.Throws<InvalidOperationException>(() => new WindowsClipboardBridge(
            adapter,
            dispatcher,
            (_, _) => ValueTask.CompletedTask));

        Assert.Equal("dispatcher failed", exception.Message);
        Assert.Equal(0, adapter.SubscriptionCount);
    }

    [Fact]
    public async Task Dispose_continues_cleanup_after_adapter_unsubscribe_failure_and_is_idempotent()
    {
        var adapter = new FailingUnsubscribeClipboardAdapter();
        var bridge = new WindowsClipboardBridge(
            adapter,
            new InlineDispatcher(),
            (_, _) => ValueTask.CompletedTask);

        adapter.SetExternalText("queued");
        await adapter.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var first = await Assert.ThrowsAsync<AggregateException>(() => bridge.DisposeAsync().AsTask());
        var second = await Assert.ThrowsAsync<AggregateException>(() => bridge.DisposeAsync().AsTask());

        Assert.Contains(first.InnerExceptions, exception => exception.Message == "unsubscribe failed");
        Assert.Same(first, second);
        Assert.Equal(0, adapter.SubscriptionCount);
        Assert.Equal(1, adapter.UnsubscribeAttempts);
        await adapter.ReadCanceled.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Dispose_continues_cleanup_after_dispatcher_failure_and_is_idempotent()
    {
        var adapter = new BlockingClipboardAdapter();
        var dispatcher = new SwitchableDispatcher();
        var bridge = new WindowsClipboardBridge(
            adapter,
            dispatcher,
            (_, _) => ValueTask.CompletedTask);

        adapter.SetExternalText("queued");
        await adapter.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
        dispatcher.Fail = true;

        var first = await Assert.ThrowsAsync<AggregateException>(() => bridge.DisposeAsync().AsTask());
        var second = await Assert.ThrowsAsync<AggregateException>(() => bridge.DisposeAsync().AsTask());

        Assert.Contains(first.InnerExceptions, exception => exception.Message == "dispatcher failed");
        Assert.Same(first, second);
        Assert.Equal(1, dispatcher.FailureCount);
    }

    [Fact]
    public async Task Disabling_bridge_drops_an_already_queued_local_change()
    {
        var adapter = new BlockingClipboardAdapter();
        var sent = new List<string>();
        await using var bridge = new WindowsClipboardBridge(
            adapter,
            new InlineDispatcher(),
            (text, _) => { sent.Add(text); return ValueTask.CompletedTask; });

        adapter.SetExternalText("queued");
        await adapter.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));
        bridge.IsEnabled = false;
        adapter.ReleaseRead();
        await bridge.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(sent);
    }

    private sealed class TestClipboardAdapter : IWindowsClipboardAdapter
    {
        private EventHandler<object>? _changed;

        public string Text { get; private set; } = string.Empty;
        public int SubscriptionCount { get; private set; }

        public event EventHandler<object>? Changed
        {
            add { _changed += value; SubscriptionCount++; }
            remove { _changed -= value; SubscriptionCount--; }
        }

        public ValueTask<string?> GetTextAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(Text);

        public void SetText(string text)
        {
            Text = text;
            _changed?.Invoke(this, new object());
        }

        public void SetExternalText(string text) => SetText(text);
    }

    private sealed class BlockingClipboardAdapter : IWindowsClipboardAdapter
    {
        private readonly TaskCompletionSource _readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EventHandler<object>? _changed;
        private string _text = string.Empty;

        public event EventHandler<object>? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public Task ReadStarted => _readStarted.Task;

        public async ValueTask<string?> GetTextAsync(CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult();
            await _releaseRead.Task.WaitAsync(cancellationToken);
            return _text;
        }

        public void SetText(string text)
        {
            _text = text;
            _changed?.Invoke(this, new object());
        }

        public void SetExternalText(string text) => SetText(text);

        public void ReleaseRead() => _releaseRead.TrySetResult();
    }

    private sealed class FailingUnsubscribeClipboardAdapter : IWindowsClipboardAdapter
    {
        private readonly TaskCompletionSource _readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readCanceled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EventHandler<object>? _changed;
        private int _subscriptionCount;

        public event EventHandler<object>? Changed
        {
            add
            {
                _changed += value;
                _subscriptionCount++;
            }
            remove
            {
                _changed -= value;
                _subscriptionCount--;
                UnsubscribeAttempts++;
                throw new InvalidOperationException("unsubscribe failed");
            }
        }

        public Task ReadStarted => _readStarted.Task;

        public Task ReadCanceled => _readCanceled.Task;

        public int SubscriptionCount => _subscriptionCount;

        public int UnsubscribeAttempts { get; private set; }

        public async ValueTask<string?> GetTextAsync(CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
            catch (OperationCanceledException)
            {
                _readCanceled.TrySetResult();
                throw;
            }
        }

        public void SetText(string text)
        {
            _changed?.Invoke(this, new object());
        }

        public void SetExternalText(string text) => SetText(text);
    }

    private sealed class ThreadRecordingClipboardAdapter : IWindowsClipboardAdapter
    {
        private readonly List<int> _readThreadIds = [];
        private readonly List<int> _writeThreadIds = [];
        private EventHandler<object>? _changed;
        private string _text = string.Empty;

        public event EventHandler<object>? Changed
        {
            add
            {
                SubscriptionThreadId = Environment.CurrentManagedThreadId;
                _changed += value;
            }
            remove
            {
                UnsubscriptionThreadId = Environment.CurrentManagedThreadId;
                _changed -= value;
            }
        }

        public int SubscriptionThreadId { get; private set; }

        public int UnsubscriptionThreadId { get; private set; }

        public IReadOnlyList<int> ReadThreadIds => _readThreadIds;

        public IReadOnlyList<int> WriteThreadIds => _writeThreadIds;

        public ValueTask<string?> GetTextAsync(CancellationToken cancellationToken)
        {
            _readThreadIds.Add(Environment.CurrentManagedThreadId);
            return ValueTask.FromResult<string?>(_text);
        }

        public void SetText(string text)
        {
            _writeThreadIds.Add(Environment.CurrentManagedThreadId);
            _text = text;
            _changed?.Invoke(this, new object());
        }

        public void SetExternalText(string text)
        {
            _text = text;
            _changed?.Invoke(this, new object());
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

    private sealed class SwitchableDispatcher : IUiDispatcher
    {
        public bool Fail { get; set; }

        public int FailureCount { get; private set; }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail)
            {
                FailureCount++;
                return Task.FromException(new InvalidOperationException("dispatcher failed"));
            }

            action();
            return Task.CompletedTask;
        }
    }

    private sealed class DedicatedThreadDispatcher : IUiDispatcher, IAsyncDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<WorkItem> _workItems = [];
        private readonly TaskCompletionSource<int> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        public DedicatedThreadDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = nameof(DedicatedThreadDispatcher),
            };
            _thread.Start();
            ThreadId = _started.Task.GetAwaiter().GetResult();
        }

        public int ThreadId { get; }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Environment.CurrentManagedThreadId == ThreadId)
            {
                action();
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _workItems.Add(new WorkItem(action, completion, cancellationToken), cancellationToken);
            return completion.Task;
        }

        public ValueTask DisposeAsync()
        {
            _workItems.CompleteAdding();
            _thread.Join();
            _workItems.Dispose();
            return ValueTask.CompletedTask;
        }

        private void Run()
        {
            _started.TrySetResult(Environment.CurrentManagedThreadId);
            foreach (var workItem in _workItems.GetConsumingEnumerable())
            {
                if (workItem.CancellationToken.IsCancellationRequested)
                {
                    workItem.Completion.TrySetCanceled(workItem.CancellationToken);
                    continue;
                }

                try
                {
                    workItem.Action();
                    workItem.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    workItem.Completion.TrySetException(exception);
                }
            }
        }

        private sealed record WorkItem(
            Action Action,
            TaskCompletionSource Completion,
            CancellationToken CancellationToken);
    }
}
