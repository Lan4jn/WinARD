namespace WinARD.Remote.Protocol.Authentication;

public sealed record ArdChallenge
{
    private readonly byte[] _modulus;
    private readonly byte[] _serverPublicKey;

    public ArdChallenge(ushort generator, int keyLength, byte[] modulus, byte[] serverPublicKey)
    {
        ArgumentNullException.ThrowIfNull(modulus);
        ArgumentNullException.ThrowIfNull(serverPublicKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyLength);
        if (modulus.Length != keyLength || serverPublicKey.Length != keyLength)
        {
            throw new ArgumentException("The modulus and server public key must match the negotiated key length.");
        }

        Generator = generator;
        KeyLength = keyLength;
        _modulus = (byte[])modulus.Clone();
        _serverPublicKey = (byte[])serverPublicKey.Clone();
    }

    public ushort Generator { get; }

    public int KeyLength { get; }

    public byte[] Modulus => (byte[])_modulus.Clone();

    public byte[] ServerPublicKey => (byte[])_serverPublicKey.Clone();

    internal ReadOnlySpan<byte> ModulusSpan => _modulus;

    internal ReadOnlySpan<byte> ServerPublicKeySpan => _serverPublicKey;
}
