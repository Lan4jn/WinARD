using WinARD.Application.Ports;
using Xunit;

#pragma warning disable CA1707, CA2215

namespace WinARD.Transport.Tests;

public sealed class TransportConnectionTests
{
    [Fact]
    public async Task Concurrent_disposal_runs_stream_and_lifetime_cleanup_once()
    {
        var stream = new PausingDisposeStream();
        var lifetime = new CountingLifetime();
        var connection = new TransportConnection(
            stream,
            new EndPointDescription("mac.example", 5900),
            lifetime);

        var first = connection.DisposeAsync().AsTask();
        await stream.Started.Task;
        var second = connection.DisposeAsync().AsTask();

        Assert.Equal(1, stream.DisposeCount);
        stream.Continue.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Disposal_preserves_stream_and_lifetime_failures()
    {
        var streamFailure = new IOException("Stream disposal failed.");
        var lifetimeFailure = new InvalidOperationException("Lifetime disposal failed.");
        var connection = new TransportConnection(
            new ThrowingDisposeStream(streamFailure),
            new EndPointDescription("mac.example", 5900),
            new ThrowingLifetime(lifetimeFailure));

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => connection.DisposeAsync().AsTask());

        Assert.Collection(
            exception.InnerExceptions,
            item => Assert.Same(streamFailure, item),
            item => Assert.Same(lifetimeFailure, item));
    }

    private sealed class PausingDisposeStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Continue { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            DisposeCount++;
            Started.TrySetResult();
            await Continue.Task;
            await base.DisposeAsync();
        }
    }

    private sealed class ThrowingDisposeStream(Exception exception) : MemoryStream
    {
        public override ValueTask DisposeAsync() => ValueTask.FromException(exception);
    }

    private sealed class CountingLifetime : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingLifetime(Exception exception) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(exception);
    }
}
