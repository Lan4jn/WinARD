using System.Buffers.Binary;
using WinARD.Remote.Protocol.Errors;

namespace WinARD.Remote.Protocol.Ard;

public static class ArdServerInitParser
{
    private const int ExtendedHeaderLength = 22;

    public static ArdExtendedServerInit Parse(ReadOnlySpan<byte> nameField)
    {
        if (nameField.Length < ExtendedHeaderLength || nameField[0] != 0)
        {
            throw new ArdExtendedInitializationRequiredException();
        }

        var capabilities = new ArdServerCapabilities(
            BinaryPrimitives.ReadUInt32BigEndian(nameField[2..6]),
            nameField[6..ExtendedHeaderLength]);

        if (!capabilities.MayControl)
        {
            throw new ArdControlNotAllowedException(capabilities.RawFlags);
        }

        var lastNulIndex = nameField.LastIndexOf((byte)0);
        return new ArdExtendedServerInit(capabilities, nameField[(lastNulIndex + 1)..]);
    }
}

public sealed class ArdExtendedServerInit
{
    private readonly byte[] _displayNameBytes;

    public ArdExtendedServerInit(ArdServerCapabilities capabilities, ReadOnlySpan<byte> displayNameBytes)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        Capabilities = capabilities;
        _displayNameBytes = displayNameBytes.ToArray();
    }

    public ArdServerCapabilities Capabilities { get; }

    public ReadOnlyMemory<byte> DisplayNameBytes => _displayNameBytes;
}
