using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using System.Text;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Testing.Rfb;
using WinARD.Testing.Streams;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Authentication;

public sealed class ArdAuthenticatorTests
{
    [Fact]
    public async Task Authenticates_and_sends_encrypted_credentials_before_fixed_width_client_public_key()
    {
        await using var server = ArdServerFixture.Create(maxReadChunk: 1);
        using var username = SecretMaterial.FromUtf8("operator");
        using var password = SecretMaterial.FromUtf8("pässword");
        var authenticator = new ArdAuthenticator(new PrivateExponentTwoRandomSource());

        await authenticator.AuthenticateAsync(server.ClientStream, RfbVersion.V3_8, username, password, CancellationToken.None);

        Assert.Equal(128 + server.KeyLength, server.ReceivedBytes.Length);
        var credentials = server.DecryptCredentials();
        Assert.Equal("operator", credentials.Username);
        Assert.Equal("pässword", credentials.Password);
        Assert.Equal(server.KeyLength, server.GetClientPublicKey().Length);
        Assert.Equal(0, server.GetClientPublicKey()[0]);
        Assert.Equal(server.KeyLength, server.GetSharedSecret().Length);
        Assert.Equal(0, server.GetSharedSecret()[0]);
    }

    [Fact]
    public async Task Accepts_credentials_that_are_exactly_63_utf8_bytes()
    {
        await using var server = ArdServerFixture.Create();
        var expectedUsername = new string('u', 63);
        var expectedPassword = new string('p', 63);
        using var username = SecretMaterial.FromUtf8(expectedUsername);
        using var password = SecretMaterial.FromUtf8(expectedPassword);

        await new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
            server.ClientStream,
            RfbVersion.V3_8,
            username,
            password,
            CancellationToken.None);

        var credentials = server.DecryptCredentials();
        Assert.Equal(expectedUsername, credentials.Username);
        Assert.Equal(expectedPassword, credentials.Password);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rejects_64_byte_credentials_before_any_network_write(bool overlongUsername)
    {
        await using var server = ArdServerFixture.Create();
        using var username = SecretMaterial.FromUtf8(overlongUsername ? new string('u', 64) : "user");
        using var password = SecretMaterial.FromUtf8(overlongUsername ? "password" : new string('p', 64));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                server.ClientStream,
                RfbVersion.V3_8,
                username,
                password,
                CancellationToken.None));

        Assert.Empty(server.ReceivedBytes);
    }

    [Fact]
    public async Task Applies_credential_limit_to_utf8_bytes_not_characters()
    {
        await using var rejectedServer = ArdServerFixture.Create();
        using var rejectedUsername = SecretMaterial.FromUtf8(new string('é', 32));
        using var password = SecretMaterial.FromUtf8("password");
        var authenticator = new ArdAuthenticator(new PrivateExponentTwoRandomSource());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            authenticator.AuthenticateAsync(
                rejectedServer.ClientStream,
                RfbVersion.V3_8,
                rejectedUsername,
                password,
                CancellationToken.None));
        Assert.Empty(rejectedServer.ReceivedBytes);

        await using var acceptedServer = ArdServerFixture.Create();
        var expected = new string('é', 31);
        using var acceptedUsername = SecretMaterial.FromUtf8(expected);
        await authenticator.AuthenticateAsync(
            acceptedServer.ClientStream,
            RfbVersion.V3_8,
            acceptedUsername,
            password,
            CancellationToken.None);
        Assert.Equal(expected, acceptedServer.DecryptCredentials().Username);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Rejects_embedded_nul_or_invalid_utf8_before_any_network_write(
        bool invalidUsername,
        bool embeddedNul)
    {
        await using var server = ArdServerFixture.Create();
        var invalidBytes = embeddedNul ? new byte[] { (byte)'a', 0, (byte)'b' } : new byte[] { 0xc3, 0x28 };
        using var username = SecretMaterial.FromBytes(invalidUsername ? invalidBytes : Encoding.UTF8.GetBytes("user"));
        using var password = SecretMaterial.FromBytes(invalidUsername ? Encoding.UTF8.GetBytes("password") : invalidBytes);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                server.ClientStream,
                RfbVersion.V3_8,
                username,
                password,
                CancellationToken.None));

        Assert.Empty(server.ReceivedBytes);
    }

    [Fact]
    public async Task Rejects_a_short_challenge()
    {
        await using var server = ArdServerFixture.ForServerBytes([0, 5, 0, 64, 0xff]);

        await Assert.ThrowsAsync<RfbProtocolException>(() => AuthenticateWithEmptyCredentialsAsync(server.ClientStream));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(513)]
    public async Task Rejects_key_length_outside_initial_safe_range_before_reading_payload(int keyLength)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(header, 5);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), checked((ushort)keyLength));
        await using var server = ArdServerFixture.ForServerBytes(header);

        await Assert.ThrowsAsync<RfbProtocolException>(() => AuthenticateWithEmptyCredentialsAsync(server.ClientStream));

        Assert.Equal(4, server.ServerBytesRead);
        Assert.Empty(server.ReceivedBytes);
    }

    [Fact]
    public async Task Rejects_generator_below_two()
    {
        await using var valid = ArdServerFixture.Create();
        var modulus = valid.Modulus;
        var serverPublic = ToFixedWidth(new BigInteger(2), modulus.Length);
        var challenge = ArdServerFixture.EncodeChallenge(1, checked((ushort)modulus.Length), modulus, serverPublic);
        await using var server = ArdServerFixture.ForServerBytes(challenge);

        await Assert.ThrowsAsync<RfbProtocolException>(() => AuthenticateWithEmptyCredentialsAsync(server.ClientStream));
        Assert.Empty(server.ReceivedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejects_zero_or_even_modulus(bool evenNonZero)
    {
        var modulus = new byte[64];
        if (evenNonZero)
        {
            modulus[0] = 0x80;
            modulus[^1] = 2;
        }

        var serverPublic = ToFixedWidth(new BigInteger(2), modulus.Length);
        var challenge = ArdServerFixture.EncodeChallenge(5, 64, modulus, serverPublic);
        await using var server = ArdServerFixture.ForServerBytes(challenge);

        await Assert.ThrowsAsync<RfbProtocolException>(() => AuthenticateWithEmptyCredentialsAsync(server.ClientStream));
        Assert.Empty(server.ReceivedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(-2)]
    public async Task Rejects_server_public_key_outside_two_through_p_minus_two(int valueSelector)
    {
        await using var valid = ArdServerFixture.Create();
        var modulus = valid.Modulus;
        var prime = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var value = valueSelector >= 0 ? new BigInteger(valueSelector) : prime + valueSelector + 1;
        var serverPublic = ToFixedWidth(value, modulus.Length);
        var challenge = ArdServerFixture.EncodeChallenge(5, checked((ushort)modulus.Length), modulus, serverPublic);
        await using var server = ArdServerFixture.ForServerBytes(challenge);

        await Assert.ThrowsAsync<RfbProtocolException>(() => AuthenticateWithEmptyCredentialsAsync(server.ClientStream));
        Assert.Empty(server.ReceivedBytes);
    }

    [Fact]
    public async Task Stops_private_exponent_rejection_sampling_after_a_bounded_number_of_attempts()
    {
        await using var server = ArdServerFixture.Create();
        var random = new RejectingRandomSource();
        using var username = SecretMaterial.FromUtf8("user");
        using var password = SecretMaterial.FromUtf8("bounded-test-password");

        var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
            new ArdAuthenticator(random).AuthenticateAsync(
                server.ClientStream,
                RfbVersion.V3_8,
                username,
                password,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(128, random.FillCount);
        Assert.Empty(server.ReceivedBytes);
        Assert.DoesNotContain("bounded-test-password", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Masks_unused_high_bits_before_private_exponent_rejection_sampling()
    {
        var modulus = new byte[64];
        modulus[0] = 1;
        modulus[^1] = 1;
        var serverPublic = ToFixedWidth(new BigInteger(2), modulus.Length);
        var challenge = ArdServerFixture.EncodeChallenge(5, 64, modulus, serverPublic);
        var serverBytes = new List<byte>(challenge);
        serverBytes.AddRange([0, 0, 0, 0]);
        await using var server = ArdServerFixture.ForServerBytes(serverBytes.ToArray());
        var random = new HighByteRandomSource();
        using var username = SecretMaterial.FromUtf8(string.Empty);
        using var password = SecretMaterial.FromUtf8(string.Empty);

        await new ArdAuthenticator(random).AuthenticateAsync(
            server.ClientStream,
            RfbVersion.V3_8,
            username,
            password,
            CancellationToken.None);

        Assert.Equal(2, random.FillCount);
    }

    [Fact]
    public async Task Accepts_zero_security_result()
    {
        await using var server = ArdServerFixture.Create(securityResult: 0);

        await AuthenticateWithEmptyCredentialsAsync(server.ClientStream);
    }

    [Fact]
    public async Task Reports_safe_rfb_38_failure_reason_and_preserves_unknown_result_code()
    {
        const string testPassword = "never-print-this-password";
        var reasonBytes = new List<byte>(Encoding.UTF8.GetBytes("Denied\ninvalid:\u001b[31m"))
        {
            0xc3,
            0x28,
        };
        await using var server = ArdServerFixture.Create(securityResult: 0xdeadbeef, reasonBytes: reasonBytes.ToArray());
        using var username = SecretMaterial.FromUtf8("user");
        using var password = SecretMaterial.FromUtf8(testPassword);

        var exception = await Assert.ThrowsAsync<ArdAuthenticationRejectedException>(() =>
            new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                server.ClientStream,
                RfbVersion.V3_8,
                username,
                password,
                CancellationToken.None));

        Assert.Equal(0xdeadbeefu, exception.ResultCode);
        Assert.Equal("Denied\\u000Ainvalid:\\u001B[31m�(", exception.Reason);
        Assert.False(exception.IsReasonTruncated);
        Assert.DoesNotContain('\n', exception.Message);
        Assert.DoesNotContain('\u001b', exception.Message);
        Assert.DoesNotContain(testPassword, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncates_rfb_38_failure_reason_at_rune_boundary_after_consuming_it()
    {
        var reason = string.Concat(Enumerable.Repeat("😀", 4097));
        var reasonBytes = Encoding.UTF8.GetBytes(reason);
        await using var server = ArdServerFixture.Create(securityResult: 1, reasonBytes: reasonBytes);

        var exception = await Assert.ThrowsAsync<ArdAuthenticationRejectedException>(() =>
            AuthenticateWithEmptyCredentialsAsync(server.ClientStream));

        Assert.True(exception.IsReasonTruncated);
        Assert.Equal(4096, exception.Reason!.EnumerateRunes().Count());
        Assert.Equal(4 + (2 * server.KeyLength) + sizeof(uint) + sizeof(uint) + reasonBytes.Length, server.ServerBytesRead);
    }

    [Theory]
    [MemberData(nameof(VersionsWithoutAuthenticationFailureReason))]
    public async Task Does_not_read_failure_reason_for_rfb_33_or_37(RfbVersion version)
    {
        var trailingReason = Encoding.UTF8.GetBytes("must remain unread");
        await using var server = ArdServerFixture.Create(securityResult: 7, reasonBytes: trailingReason);
        using var username = SecretMaterial.FromUtf8(string.Empty);
        using var password = SecretMaterial.FromUtf8(string.Empty);

        var exception = await Assert.ThrowsAsync<ArdAuthenticationRejectedException>(() =>
            new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                server.ClientStream,
                version,
                username,
                password,
                CancellationToken.None));

        Assert.Equal(7u, exception.ResultCode);
        Assert.Null(exception.Reason);
        Assert.Equal(4 + (2 * server.KeyLength) + sizeof(uint), server.ServerBytesRead);
    }

    [Fact]
    public async Task Does_not_flush_or_dispose_stream()
    {
        await using var server = ArdServerFixture.Create();

        await AuthenticateWithEmptyCredentialsAsync(server.ClientStream);

        Assert.Equal(0, server.FlushCount);
        Assert.False(server.IsDisposed);
    }

    [Fact]
    public async Task Propagates_pre_cancelled_token_without_network_activity()
    {
        await using var server = ArdServerFixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AuthenticateWithEmptyCredentialsAsync(server.ClientStream, cancellation.Token));

        Assert.Equal(0, server.ServerBytesRead);
        Assert.Empty(server.ReceivedBytes);
    }

    [Fact]
    public async Task Propagates_cancellation_to_pending_read()
    {
        await using var stream = new BlockingReadStream();
        using var cancellation = new CancellationTokenSource();
        var authenticationTask = AuthenticateWithEmptyCredentialsAsync(stream, cancellation.Token);

        try
        {
            await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                authenticationTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, stream.ReceivedCancellationToken);
        }
        finally
        {
            await stream.DisposeAsync();
            await ObserveCompletionAsync(authenticationTask);
        }
    }

    [Fact]
    public async Task Rejects_null_or_disposed_arguments_before_reading_challenge()
    {
        using var valid = SecretMaterial.FromUtf8(string.Empty);
        var disposed = SecretMaterial.FromUtf8("disposed");
        disposed.Dispose();
        var authenticator = new ArdAuthenticator(new PrivateExponentTwoRandomSource());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            authenticator.AuthenticateAsync(null!, RfbVersion.V3_8, valid, valid, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            authenticator.AuthenticateAsync(Stream.Null, null!, valid, valid, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            authenticator.AuthenticateAsync(Stream.Null, RfbVersion.V3_8, null!, valid, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            authenticator.AuthenticateAsync(Stream.Null, RfbVersion.V3_8, valid, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            authenticator.AuthenticateAsync(Stream.Null, RfbVersion.V3_8, disposed, valid, CancellationToken.None));
    }

    [Fact]
    public void Secret_material_copies_input_rejects_small_destinations_and_does_not_leak_from_tostring()
    {
        var input = Encoding.UTF8.GetBytes("copy-me-secret");
        using var secret = SecretMaterial.FromBytes(input);
        input.AsSpan().Clear();
        var copy = new byte[secret.Length];

        secret.CopyTo(copy);

        Assert.Equal("copy-me-secret", Encoding.UTF8.GetString(copy));
        Assert.Throws<ArgumentException>(() => secret.CopyTo(new byte[secret.Length - 1]));
        Assert.DoesNotContain("copy-me-secret", secret.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_material_can_encode_a_character_span_without_retaining_the_caller_buffer()
    {
        var characters = "span-secret".ToCharArray();
        using var secret = SecretMaterial.FromUtf8(characters);
        characters.AsSpan().Clear();
        var copy = new byte[secret.Length];

        secret.CopyTo(copy);

        Assert.Equal("span-secret", Encoding.UTF8.GetString(copy));
    }

    [Fact]
    public void Secret_material_zeroes_storage_and_rejects_copy_after_dispose()
    {
        var secret = SecretMaterial.FromUtf8("dispose-me-secret");
        var field = typeof(SecretMaterial).GetField("_bytes", BindingFlags.Instance | BindingFlags.NonPublic);
        var storage = Assert.IsType<byte[]>(field!.GetValue(secret));

        secret.Dispose();

        Assert.All(storage, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => secret.CopyTo(new byte[storage.Length]));
    }

    [Fact]
    public void Challenge_defensively_copies_arrays_and_validates_lengths()
    {
        var modulus = new byte[] { 1, 2 };
        var serverPublic = new byte[] { 3, 4 };
        var challenge = new ArdChallenge(5, 2, modulus, serverPublic);
        modulus[0] = 9;
        serverPublic[0] = 9;

        Assert.Equal(new byte[] { 1, 2 }, challenge.Modulus);
        Assert.Equal(new byte[] { 3, 4 }, challenge.ServerPublicKey);
        var exposed = challenge.Modulus;
        exposed[0] = 8;
        Assert.Equal(1, challenge.Modulus[0]);
        Assert.Throws<ArgumentException>(() => new ArdChallenge(5, 2, [1], [2, 3]));
    }

    [Fact]
    public void Response_defensively_copies_arrays()
    {
        var encrypted = new byte[] { 1, 2 };
        var clientPublic = new byte[] { 3, 4 };
        var response = new ArdResponse(encrypted, clientPublic);
        encrypted[0] = 9;
        clientPublic[0] = 9;

        Assert.Equal(new byte[] { 1, 2 }, response.EncryptedCredentials);
        Assert.Equal(new byte[] { 3, 4 }, response.ClientPublicKey);
        var exposed = response.ClientPublicKey;
        exposed[0] = 8;
        Assert.Equal(3, response.ClientPublicKey[0]);
    }

    public static TheoryData<RfbVersion> VersionsWithoutAuthenticationFailureReason =>
        new()
        {
            RfbVersion.V3_3,
            RfbVersion.V3_7,
        };

    private static async Task AuthenticateWithEmptyCredentialsAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var username = SecretMaterial.FromUtf8(string.Empty);
        using var password = SecretMaterial.FromUtf8(string.Empty);
        await new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
            stream,
            RfbVersion.V3_8,
            username,
            password,
            cancellationToken);
    }

    private static byte[] ToFixedWidth(BigInteger value, int width)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[width];
        bytes.CopyTo(result, result.Length - bytes.Length);
        return result;
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class PrivateExponentTwoRandomSource : IRandomSource
    {
        private int _fillCount;

        public void Fill(Span<byte> destination)
        {
            destination.Fill(_fillCount++ == 0 ? (byte)0 : (byte)0xa5);
        }
    }

    private sealed class RejectingRandomSource : IRandomSource
    {
        public int FillCount { get; private set; }

        public void Fill(Span<byte> destination)
        {
            FillCount++;
            destination.Fill(0xff);
        }
    }

    private sealed class HighByteRandomSource : IRandomSource
    {
        public int FillCount { get; private set; }

        public void Fill(Span<byte> destination)
        {
            FillCount++;
            destination.Clear();
            destination[0] = 1;
        }
    }
}
