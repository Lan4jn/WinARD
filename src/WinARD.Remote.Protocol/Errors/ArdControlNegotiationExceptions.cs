namespace WinARD.Remote.Protocol.Errors;

public sealed class ArdExtendedInitializationRequiredException : Exception
{
    public ArdExtendedInitializationRequiredException()
        : base("Apple Remote Desktop extended server initialization is required for control.")
    {
    }
}

public sealed class ArdControlNotAllowedException : Exception
{
    public ArdControlNotAllowedException(uint rawServerFlags)
        : base($"Apple Remote Desktop server does not allow control (flags 0x{rawServerFlags:X8}).")
    {
        RawServerFlags = rawServerFlags;
    }

    public uint RawServerFlags { get; }
}

public sealed class ArdSessionCommandUnavailableException : Exception
{
    public ArdSessionCommandUnavailableException()
        : base("Apple Remote Desktop session command is unavailable.")
    {
    }
}

public sealed class ArdSessionDeniedException : Exception
{
    public ArdSessionDeniedException(uint status)
        : base($"Apple Remote Desktop session was denied (status 0x{status:X8}).")
    {
        Status = status;
    }

    public uint Status { get; }
}

public sealed class ArdSessionMalformedException : Exception
{
    public ArdSessionMalformedException()
        : base("Apple Remote Desktop session response was malformed.")
    {
    }
}
