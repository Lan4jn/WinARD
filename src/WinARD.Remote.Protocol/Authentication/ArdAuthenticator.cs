using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Authentication;

public sealed class ArdAuthenticator
{
    private const int CredentialFieldLength = 64;
    private const int CredentialPlaintextLength = CredentialFieldLength * 2;
    private const int MaximumCredentialByteLength = CredentialFieldLength - 1;
    private const int MinimumKeyLength = 64;
    private const int MaximumKeyLength = 512;
    private const int MaximumPrivateExponentAttempts = 128;

    private readonly IRandomSource _randomSource;

    public ArdAuthenticator()
        : this(new CryptoRandomSource())
    {
    }

    public ArdAuthenticator(IRandomSource randomSource)
    {
        ArgumentNullException.ThrowIfNull(randomSource);
        _randomSource = randomSource;
    }

    public Task AuthenticateAsync(
        Stream stream,
        RfbVersion version,
        ISecretMaterial username,
        ISecretMaterial password,
        CancellationToken cancellationToken) =>
        AuthenticateAsync(stream, version, username, password, ProtocolLimits.Default, cancellationToken);

    public async Task AuthenticateAsync(
        Stream stream,
        RfbVersion version,
        ISecretMaterial username,
        ISecretMaterial password,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();

        byte[]? usernameBytes = null;
        byte[]? passwordBytes = null;
        try
        {
            usernameBytes = CopyCredential(username, nameof(username));
            passwordBytes = CopyCredential(password, nameof(password));

            var reader = new RfbReader(stream, limits);
            var writer = new RfbWriter(stream);
            var challenge = await ReadChallengeAsync(reader, cancellationToken);
            var response = CreateResponse(challenge, usernameBytes, passwordBytes);

            await writer.WriteBytesAsync(response.EncryptedCredentialsMemory, cancellationToken);
            await writer.WriteBytesAsync(response.ClientPublicKeyMemory, cancellationToken);
            await ReadSecurityResultAsync(
                reader,
                version,
                limits,
                usernameBytes,
                passwordBytes,
                cancellationToken);
        }
        finally
        {
            ZeroMemory(usernameBytes);
            ZeroMemory(passwordBytes);
        }
    }

    private static async ValueTask<ArdChallenge> ReadChallengeAsync(
        RfbReader reader,
        CancellationToken cancellationToken)
    {
        var generator = await reader.ReadUInt16Async(cancellationToken);
        var keyLength = await reader.ReadUInt16Async(cancellationToken);
        if (keyLength is < MinimumKeyLength or > MaximumKeyLength)
        {
            throw new RfbProtocolException(
                $"ARD key length {keyLength} is outside the supported range of {MinimumKeyLength} to {MaximumKeyLength} bytes.");
        }

        var modulus = await reader.ReadBytesAsync(keyLength, cancellationToken);
        var serverPublicKey = await reader.ReadBytesAsync(keyLength, cancellationToken);
        var challenge = new ArdChallenge(generator, keyLength, modulus, serverPublicKey);
        ValidateChallenge(challenge);
        return challenge;
    }

    private ArdResponse CreateResponse(
        ArdChallenge challenge,
        ReadOnlySpan<byte> username,
        ReadOnlySpan<byte> password)
    {
        var privateExponentBytes = new byte[challenge.KeyLength];
        var sharedSecret = new byte[challenge.KeyLength];
        var aesKey = new byte[16];
        var plaintextCredentials = new byte[CredentialPlaintextLength];
        var encryptedCredentials = new byte[CredentialPlaintextLength];
        var clientPublicKey = new byte[challenge.KeyLength];

        try
        {
            ComputeDhValues(challenge, privateExponentBytes, clientPublicKey, sharedSecret);
            DeriveAesKey(sharedSecret, aesKey);
            CreatePlaintextCredentials(username, password, plaintextCredentials);
            EncryptCredentials(plaintextCredentials, aesKey, encryptedCredentials);
            return new ArdResponse(encryptedCredentials, clientPublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateExponentBytes);
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(aesKey);
            CryptographicOperations.ZeroMemory(plaintextCredentials);
            CryptographicOperations.ZeroMemory(encryptedCredentials);
            CryptographicOperations.ZeroMemory(clientPublicKey);
        }
    }

    private void ComputeDhValues(
        ArdChallenge challenge,
        Span<byte> privateExponentBytes,
        Span<byte> clientPublicKey,
        Span<byte> sharedSecret)
    {
        var prime = new BigInteger(challenge.ModulusSpan, isUnsigned: true, isBigEndian: true);
        var serverPublicKey = new BigInteger(challenge.ServerPublicKeySpan, isUnsigned: true, isBigEndian: true);
        var privateExponent = GeneratePrivateExponent(prime, privateExponentBytes);
        WriteFixedWidth(
            BigInteger.ModPow(new BigInteger(challenge.Generator), privateExponent, prime),
            clientPublicKey);
        WriteFixedWidth(BigInteger.ModPow(serverPublicKey, privateExponent, prime), sharedSecret);
    }

    private static void ValidateChallenge(ArdChallenge challenge)
    {
        if (challenge.Generator < 2)
        {
            throw new RfbProtocolException("The ARD DH generator must be at least 2.");
        }

        if (challenge.ModulusSpan[0] == 0)
        {
            throw new RfbProtocolException("The ARD DH modulus must use the negotiated unsigned width.");
        }

        var prime = new BigInteger(challenge.ModulusSpan, isUnsigned: true, isBigEndian: true);
        if (prime <= BigInteger.Zero || prime.IsEven)
        {
            throw new RfbProtocolException("The ARD DH modulus must be a positive odd integer.");
        }

        var serverPublicKey = new BigInteger(challenge.ServerPublicKeySpan, isUnsigned: true, isBigEndian: true);
        if (serverPublicKey < 2 || serverPublicKey > prime - 2)
        {
            throw new RfbProtocolException("The ARD server public key is outside the permitted range.");
        }
    }

    private BigInteger GeneratePrivateExponent(BigInteger prime, Span<byte> privateExponentBytes)
    {
        var sampleRange = prime - 3;
        var bitLength = checked((int)sampleRange.GetBitLength());
        var excessBits = checked((privateExponentBytes.Length * 8) - bitLength);
        var excessBytes = excessBits / 8;
        var partialExcessBits = excessBits % 8;

        for (var attempt = 0; attempt < MaximumPrivateExponentAttempts; attempt++)
        {
            _randomSource.Fill(privateExponentBytes);
            privateExponentBytes[..excessBytes].Clear();
            if (partialExcessBits != 0)
            {
                privateExponentBytes[excessBytes] &= (byte)(byte.MaxValue >> partialExcessBits);
            }

            var sample = new BigInteger(privateExponentBytes, isUnsigned: true, isBigEndian: true);
            if (sample < sampleRange)
            {
                return sample + 2;
            }
        }

        throw new RfbProtocolException(
            $"Unable to generate an ARD private exponent after {MaximumPrivateExponentAttempts} attempts.");
    }

    private void CreatePlaintextCredentials(
        ReadOnlySpan<byte> username,
        ReadOnlySpan<byte> password,
        Span<byte> plaintext)
    {
        _randomSource.Fill(plaintext);
        username.CopyTo(plaintext);
        plaintext[username.Length] = 0;
        password.CopyTo(plaintext[CredentialFieldLength..]);
        plaintext[CredentialFieldLength + password.Length] = 0;
    }

#pragma warning disable CA5351 // MD5 is required for Apple Remote Desktop security type 30 compatibility.
    private static void DeriveAesKey(ReadOnlySpan<byte> sharedSecret, Span<byte> aesKey) =>
        _ = MD5.HashData(sharedSecret, aesKey);
#pragma warning restore CA5351

#pragma warning disable CA5358 // AES-ECB is required for Apple Remote Desktop security type 30 compatibility.
    private static void EncryptCredentials(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> aesKey,
        Span<byte> ciphertext)
    {
        var keyBytes = aesKey.ToArray();
        using var aes = Aes.Create();
        try
        {
            aes.Key = keyBytes;
            _ = aes.EncryptEcb(plaintext, ciphertext, PaddingMode.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }
#pragma warning restore CA5358

    private static async ValueTask ReadSecurityResultAsync(
        RfbReader reader,
        RfbVersion version,
        ProtocolLimits limits,
        ReadOnlyMemory<byte> username,
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken)
    {
        var resultCode = await reader.ReadUInt32Async(cancellationToken);
        if (resultCode == 0)
        {
            return;
        }

        if (version == RfbVersion.V3_8)
        {
            try
            {
                var failure = await RfbFailureReasonReader.ReadAsync(
                    reader,
                    limits,
                    username,
                    password,
                    cancellationToken);
                throw new ArdAuthenticationRejectedException(resultCode, failure.Reason, failure.IsTruncated);
            }
            catch (RfbProtocolException exception)
            {
                throw new ArdAuthenticationRejectedException(resultCode, null, false, exception);
            }
        }

        throw new ArdAuthenticationRejectedException(resultCode, null, false);
    }

    private static byte[] CopyCredential(ISecretMaterial secret, string parameterName)
    {
        var length = secret.Length;
        if (length < 0)
        {
            throw new ArgumentException("Secret material reported a negative length.", parameterName);
        }

        if (length > MaximumCredentialByteLength)
        {
            throw new ArgumentException(
                $"ARD credentials must be at most {MaximumCredentialByteLength} UTF-8 bytes.",
                parameterName);
        }

        var copy = new byte[length];
        try
        {
            secret.CopyTo(copy);
            ValidateCredentialEncoding(copy, parameterName);
            return copy;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(copy);
            throw;
        }
    }

    private static void ValidateCredentialEncoding(ReadOnlySpan<byte> credential, string parameterName)
    {
        if (credential.Contains((byte)0))
        {
            throw new ArgumentException("ARD credentials cannot contain an embedded NUL byte.", parameterName);
        }

        while (!credential.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(credential, out _, out var bytesConsumed);
            if (status != OperationStatus.Done)
            {
                throw new ArgumentException("ARD credentials must contain valid UTF-8.", parameterName);
            }

            credential = credential[bytesConsumed..];
        }
    }

    private static void WriteFixedWidth(BigInteger value, Span<byte> destination)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        try
        {
            if (bytes.Length > destination.Length)
            {
                throw new RfbProtocolException("An ARD DH value exceeds the negotiated key width.");
            }

            destination.Clear();
            bytes.CopyTo(destination[^bytes.Length..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ZeroMemory(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}
