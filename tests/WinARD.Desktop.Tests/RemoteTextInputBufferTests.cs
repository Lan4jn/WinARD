using Windows.System;
using WinARD.Desktop.Input;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests;

public sealed class RemoteTextInputBufferTests
{
    [Fact]
    public void Shifted_digit_character_is_not_sent_twice()
    {
        var buffer = new RemoteTextInputBuffer();

        buffer.OnPhysicalKeyDown(VirtualKey.Number1, 0x02, isExtended: false);

        Assert.Null(buffer.AcceptCharacter('!'));
    }

    [Fact]
    public void Surrogate_pair_is_buffered_as_one_unicode_scalar()
    {
        var buffer = new RemoteTextInputBuffer();

        Assert.Null(buffer.AcceptCharacter('\ud83d'));
        Assert.Equal("😀", buffer.AcceptCharacter('\ude00'));
    }

    [Fact]
    public void Reset_discards_pending_physical_and_surrogate_state()
    {
        var buffer = new RemoteTextInputBuffer();
        buffer.OnPhysicalKeyDown(VirtualKey.Number1, 0x02, isExtended: false);
        Assert.Null(buffer.AcceptCharacter('\ud83d'));

        buffer.Reset();

        Assert.Equal("!", buffer.AcceptCharacter('!'));
        Assert.Null(buffer.AcceptCharacter('\ude00'));
    }
}
