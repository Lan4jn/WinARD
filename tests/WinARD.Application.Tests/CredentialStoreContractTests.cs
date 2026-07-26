using WinARD.Application.Ports;
using Xunit;

namespace WinARD.Application.Tests;

public sealed class CredentialStoreContractTests
{
    [Fact]
    public void VersionReturningCompareExchangeMustBeImplementedAtomicallyByEveryStore()
    {
        var method = typeof(ICredentialStore).GetMethod(
            nameof(ICredentialStore.CompareExchangeWithVersionAsync));

        Assert.NotNull(method);
        Assert.True(method.IsAbstract);
    }
}
