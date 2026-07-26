using WinARD.Desktop.Threading;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Threading;

public sealed class AsyncUiOperationTests
{
    [Fact]
    public async Task RunAsync_observes_show_failure_without_rethrowing()
    {
        var operation = new AsyncUiOperation();
        var failure = new InvalidOperationException("ShowAsync failed");

        await operation.RunAsync(() => Task.FromException(failure), CancellationToken.None);

        Assert.Equal([failure], operation.Errors);
    }
}
