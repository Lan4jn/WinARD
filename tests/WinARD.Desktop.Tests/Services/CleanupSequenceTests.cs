using WinARD.Desktop.Services;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Services;

public sealed class CleanupSequenceTests
{
    [Fact]
    public async Task Runs_every_cleanup_and_aggregates_failures_in_order()
    {
        var calls = new List<int>();

        var failure = await Assert.ThrowsAsync<AggregateException>(() => CleanupSequence.RunAsync(
            () => { calls.Add(1); return Task.FromException(new IOException("one")); },
            () => { calls.Add(2); return Task.CompletedTask; },
            () => { calls.Add(3); return Task.FromException(new InvalidOperationException("three")); }));

        Assert.Equal([1, 2, 3], calls);
        Assert.Collection(failure.InnerExceptions,
            item => Assert.IsType<IOException>(item),
            item => Assert.IsType<InvalidOperationException>(item));
    }
}
