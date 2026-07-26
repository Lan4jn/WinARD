using System.Text;
using WinARD.Security.Secrets;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Security.Tests;

public sealed class SecretBufferTests
{
    [Fact]
    public void Copy_clone_and_dispose_have_explicit_ownership()
    {
        var source = Encoding.UTF8.GetBytes("buffer-value-9d50");
        using var secret = SecretBuffer.CopyFrom(source);
        using var clone = secret.Clone();
        Array.Clear(source);

        var copy = new byte[clone.Length];
        clone.CopyTo(copy);

        Assert.Equal("buffer-value-9d50", Encoding.UTF8.GetString(copy));
        Assert.DoesNotContain("buffer-value-9d50", secret.ToString(), StringComparison.Ordinal);
        Array.Clear(copy);
    }

    [Fact]
    public void Dispose_is_idempotent_and_prevents_further_access()
    {
        var secret = SecretBuffer.CopyFrom([1, 2, 3]);

        secret.Dispose();
        secret.Dispose();

        Assert.Throws<ObjectDisposedException>(() => secret.CopyTo(new byte[3]));
        Assert.Throws<ObjectDisposedException>(() => secret.Clone());
    }
}
