using WinARD.Infrastructure.Settings;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Infrastructure.Tests;

public sealed class CredentialMutationGateTests
{
    [Fact]
    public async Task Same_database_path_coordinates_across_gate_instances()
    {
        var path = Path.Combine(Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"), "winard.db");
        using var first = new CredentialMutationGate(path);
        using var second = new CredentialMutationGate(path.ToUpperInvariant());
        using var held = await first.EnterAsync(CancellationToken.None);

        var waiting = second.EnterAsync(CancellationToken.None).AsTask();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            waiting.WaitAsync(TimeSpan.FromMilliseconds(150)));

        held.Dispose();
        using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Different_database_paths_do_not_block_each_other()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"));
        using var first = new CredentialMutationGate(Path.Combine(directory, "first.db"));
        using var second = new CredentialMutationGate(Path.Combine(directory, "second.db"));
        using var held = await first.EnterAsync(CancellationToken.None);

        using var acquired = await second.EnterAsync(CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Cancelled_wait_does_not_consume_or_leak_the_gate()
    {
        var path = Path.Combine(Path.GetTempPath(), "WinARD.Tests", Guid.NewGuid().ToString("N"), "winard.db");
        using var first = new CredentialMutationGate(path);
        using var second = new CredentialMutationGate(path);
        using var held = await first.EnterAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = second.EnterAsync(cancellation.Token).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();

        using var acquired = await second.EnterAsync(CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }
}
