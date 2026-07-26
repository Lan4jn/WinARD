using WinARD.Application.Ports;

namespace WinARD.Desktop.Services;

public sealed class UnavailableRfbClientFactory : IRfbClientFactory
{
    public IRfbClient Create(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        throw new NotSupportedException("Interactive remote sessions are not part of the device-library MVP.");
    }
}
