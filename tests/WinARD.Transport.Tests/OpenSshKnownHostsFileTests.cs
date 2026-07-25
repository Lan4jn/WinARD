using WinARD.Transport.Ssh;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Transport.Tests;

public sealed class OpenSshKnownHostsFileTests
{
    [Fact]
    public async Task Stream_dispose_failure_does_not_skip_file_deletion()
    {
        var streamFailure = new IOException("stream dispose failed");
        var stream = new TrackingDisposeStream(streamFailure);
        var deleteCount = 0;
        var file = new TemporaryOpenSshKnownHostsFile(
            @"C:\Temp\known_hosts",
            stream,
            _ => deleteCount++);

        var exception = await Assert.ThrowsAsync<IOException>(
            () => file.DisposeAsync().AsTask());

        Assert.Same(streamFailure, exception);
        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(1, deleteCount);
    }

    [Fact]
    public async Task Stream_and_delete_failures_are_both_preserved()
    {
        var streamFailure = new IOException("stream dispose failed");
        var deleteFailure = new UnauthorizedAccessException("delete failed");
        var stream = new TrackingDisposeStream(streamFailure);
        var file = new TemporaryOpenSshKnownHostsFile(
            @"C:\Temp\known_hosts",
            stream,
            _ => throw deleteFailure);

        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            () => file.DisposeAsync().AsTask());

        Assert.Same(streamFailure, aggregate.InnerExceptions[0]);
        Assert.Same(deleteFailure, aggregate.InnerExceptions[1]);
    }

    [Fact]
    public async Task Concurrent_dispose_runs_cleanup_once_and_shares_the_same_failure()
    {
        var streamFailure = new IOException("stream dispose failed");
        var stream = new TrackingDisposeStream(streamFailure);
        var deleteCount = 0;
        var file = new TemporaryOpenSshKnownHostsFile(
            @"C:\Temp\known_hosts",
            stream,
            _ => deleteCount++);

        var first = file.DisposeAsync().AsTask();
        var second = file.DisposeAsync().AsTask();

        Assert.Same(first, second);
        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAsync<IOException>(() => second);
        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(1, deleteCount);
    }

    private sealed class TrackingDisposeStream(Exception? disposeException)
        : MemoryStream
    {
        public int DisposeCount { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await base.DisposeAsync();
            if (disposeException is not null)
            {
                throw disposeException;
            }
        }
    }
}
