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
