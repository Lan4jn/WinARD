using System.Text;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.Initialization;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Ard;

public sealed class ArdServerInitParserTests
{
    [Fact]
    public void Parse_reads_extended_capabilities_and_display_name()
    {
        var field = ExtendedNameField(flags: 0x00000006, bitmap: Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), displayName: "Studio Mac");

        var result = ArdServerInitParser.Parse(field);

        Assert.Equal(0x00000006u, result.Capabilities.RawFlags);
        Assert.True(result.Capabilities.MayControl);
        Assert.True(result.Capabilities.RequiresSessionSelection);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i), result.Capabilities.CommandBitmap.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes("Studio Mac"), result.DisplayNameBytes.ToArray());
    }

    [Fact]
    public void Parse_accepts_a_nonzero_second_extended_header_byte()
    {
        var field = ExtendedNameField(flags: 0x00000002, bitmap: new byte[16], displayName: "x");
        field[1] = 0x7F;

        var result = ArdServerInitParser.Parse(field);

        Assert.True(result.Capabilities.MayControl);
    }

    [Theory]
    [InlineData(21, 0)]
    [InlineData(22, 1)]
    public void Parse_rejects_non_extended_server_initialization(int length, byte firstByte)
    {
        var field = new byte[length];
        field[0] = firstByte;

        Assert.Throws<ArdExtendedInitializationRequiredException>(() => ArdServerInitParser.Parse(field));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void Parse_rejects_servers_that_do_not_allow_control(uint flags)
    {
        var exception = Assert.Throws<ArdControlNotAllowedException>(() =>
            ArdServerInitParser.Parse(ExtendedNameField(flags, new byte[16], "ignored")));

        Assert.Equal(flags, exception.RawServerFlags);
    }

    [Fact]
    public void Parse_defensively_copies_capabilities_and_display_name()
    {
        var bitmap = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var field = ExtendedNameField(0x00000002, bitmap, "Studio Mac");

        var result = ArdServerInitParser.Parse(field);
        field[6] = 0xFF;
        field[^1] = 0xFF;

        Assert.Equal(0, result.Capabilities.CommandBitmap.Span[0]);
        Assert.Equal((byte)'c', result.DisplayNameBytes.Span[^1]);
    }

    [Fact]
    public void Parse_allows_an_empty_display_name()
    {
        var result = ArdServerInitParser.Parse(ExtendedNameField(0x00000002, new byte[16], string.Empty));

        Assert.Empty(result.DisplayNameBytes.ToArray());
    }

    [Fact]
    public void Parse_uses_bytes_after_the_last_nul_as_the_display_name()
    {
        var prefix = Encoding.UTF8.GetBytes("first\0second\0");
        byte[] field = [.. ExtendedNameField(0x00000002, new byte[16], string.Empty), .. prefix, (byte)'z'];

        var result = ArdServerInitParser.Parse(field);

        Assert.Equal([(byte)'z'], result.DisplayNameBytes.ToArray());
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void Capabilities_rejects_command_bitmaps_that_are_not_16_bytes(int length)
    {
        Assert.Throws<ArgumentException>(() => new ArdServerCapabilities(0, new byte[length]));
    }

    [Fact]
    public void Rfb_server_init_preserves_its_five_argument_constructor_and_accepts_ard_capabilities()
    {
        var oldConstructorResult = new RfbServerInit(640, 480, PixelFormat.WinArdBgra32, "Studio Mac", false);
        var capabilities = new ArdServerCapabilities(0x00000002, new byte[16]);
        var extendedResult = new RfbServerInit(640, 480, PixelFormat.WinArdBgra32, "Studio Mac", false, capabilities);

        Assert.Null(oldConstructorResult.ArdCapabilities);
        Assert.Same(capabilities, extendedResult.ArdCapabilities);
        Assert.Equal(oldConstructorResult with { ArdCapabilities = capabilities }, extendedResult);
    }

    private static byte[] ExtendedNameField(uint flags, byte[] bitmap, string displayName)
    {
        var field = new byte[23 + Encoding.UTF8.GetByteCount(displayName)];
        field[0] = 0;
        field[2] = (byte)(flags >> 24);
        field[3] = (byte)(flags >> 16);
        field[4] = (byte)(flags >> 8);
        field[5] = (byte)flags;
        bitmap.CopyTo(field, 6);
        Encoding.UTF8.GetBytes(displayName, field.AsSpan(23));
        return field;
    }
}
