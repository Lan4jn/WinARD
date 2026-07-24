namespace WinARD.Remote.Protocol.Authentication;

public sealed record ArdResponse
{
    private readonly byte[] _clientPublicKey;
    private readonly byte[] _encryptedCredentials;

    public ArdResponse(byte[] encryptedCredentials, byte[] clientPublicKey)
    {
        ArgumentNullException.ThrowIfNull(encryptedCredentials);
        ArgumentNullException.ThrowIfNull(clientPublicKey);
        _encryptedCredentials = (byte[])encryptedCredentials.Clone();
        _clientPublicKey = (byte[])clientPublicKey.Clone();
    }

    public byte[] EncryptedCredentials => (byte[])_encryptedCredentials.Clone();

    public byte[] ClientPublicKey => (byte[])_clientPublicKey.Clone();

    internal ReadOnlyMemory<byte> EncryptedCredentialsMemory => _encryptedCredentials;

    internal ReadOnlyMemory<byte> ClientPublicKeyMemory => _clientPublicKey;
}
