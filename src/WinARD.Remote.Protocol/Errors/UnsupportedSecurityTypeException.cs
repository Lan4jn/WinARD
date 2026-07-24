using System.Collections.ObjectModel;
using WinARD.Remote.Protocol.Handshake;

namespace WinARD.Remote.Protocol.Errors;

public sealed class UnsupportedSecurityTypeException : Exception
{
    public UnsupportedSecurityTypeException(RfbVersion version, IEnumerable<uint> offeredTypes)
        : base("The server did not offer the required Apple Remote Desktop security type.")
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(offeredTypes);

        Version = version;
        OfferedTypes = Array.AsReadOnly(offeredTypes.ToArray());
    }

    public RfbVersion Version { get; }

    public IReadOnlyList<uint> OfferedTypes { get; }
}
