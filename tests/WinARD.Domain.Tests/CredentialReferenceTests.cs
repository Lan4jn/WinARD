using WinARD.Domain.Security;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Domain.Tests;

public sealed class CredentialReferenceTests
{
    [Theory]
    [InlineData(" ", "key")]
    [InlineData("store", " ")]
    public void Create_rejects_blank_store_or_key(string store, string key)
    {
        Assert.Throws<ArgumentException>(() => CredentialReference.Create(store, key));
    }

    [Fact]
    public void ToString_is_stable_trimmed_and_uri_escaped_without_spaces()
    {
        var reference = CredentialReference.Create(" windows ", " mac device/1 ");

        var value = reference.ToString();

        Assert.Equal("windows", reference.Store);
        Assert.Equal("mac device/1", reference.Key);
        Assert.Equal("credential://windows/mac%20device%2F1", value);
        Assert.DoesNotContain(' ', value);
        Assert.Equal(value, reference.ToString());
    }
}
