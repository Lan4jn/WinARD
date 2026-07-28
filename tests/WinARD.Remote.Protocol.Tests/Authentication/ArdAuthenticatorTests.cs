using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using WinARD.ProtocolProbe;
using WinARD.Remote.Protocol.Authentication;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Handshake;
using WinARD.Remote.Protocol.Input;
using WinARD.Remote.Protocol.IO;
using WinARD.Testing.Rfb;
using WinARD.Testing.Streams;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Authentication;

public sealed class ArdAuthenticatorTests
{
    private const string FixtureModulusHex =
        "D2652EF10104A3DDC1219700EDFBD1E19F7678B4A4F6D5952634BD8BF1D60326322B5D32366DC25CB4E8E73AF4312A70D2DCAF2747EB89D7E88553EECD6A283D";

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
    public async Task Ard_response_is_one_message_and_cannot_interleave_with_pointer_message()
    {
        await using var stream = new BlockingFirstWriteDuplexStream(SuccessfulChallengeAndResult());
        using var username = SecretMaterial.FromUtf8("operator");
        using var password = SecretMaterial.FromUtf8("password");
        var authentication = new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
            stream,
            RfbVersion.V3_8,
            username,
            password,
            CancellationToken.None);
        await stream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var pointer = new PointerEventWriter(new RfbWriter(stream));
        var pointerWrite = pointer.WriteAsync(1, 2, 3, CancellationToken.None).AsTask();

        stream.ReleaseWrite();
        await Task.WhenAll(authentication, pointerWrite).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([192, 6], stream.WriteLengths);
        Assert.Equal(198, stream.Bytes.Count);
        Assert.Equal([5, 1, 0, 2, 0, 3], stream.Bytes.TakeLast(6));
    }

    [Fact]
    public async Task Failed_ard_response_write_zeroes_combined_sensitive_message()
    {
        await using var stream = new CapturingFailureDuplexStream(SuccessfulChallengeAndResult());
        using var username = SecretMaterial.FromUtf8("operator");
        using var password = SecretMaterial.FromUtf8("password");

        await Assert.ThrowsAsync<IOException>(() =>
            new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                stream,
                RfbVersion.V3_8,
                username,
                password,
                CancellationToken.None));

        Assert.NotNull(stream.CapturedBuffer);
        Assert.All(stream.CapturedBuffer!, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Matches_independent_python_known_answer_vector()
    {
        var modulus = Convert.FromHexString(FixtureModulusHex);
        var serverPublicKey = Convert.FromHexString(
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007D");
        var challenge = ArdServerFixture.EncodeChallenge(5, 64, modulus, serverPublicKey);
        var serverBytes = new List<byte>(challenge);
        serverBytes.AddRange([0, 0, 0, 0]);
        await using var server = ArdServerFixture.ForServerBytes(serverBytes.ToArray());
        using var username = SecretMaterial.FromUtf8("user");
        using var password = SecretMaterial.FromUtf8("password");
        var random = new KnownAnswerRandomSource();

        await new ArdAuthenticator(random).AuthenticateAsync(
            server.ClientStream,
            RfbVersion.V3_8,
            username,
            password,
            CancellationToken.None);

        var expectedCiphertext = Convert.FromHexString(
            "9AA256017F35EF39E53F21F3ADC120050F1E37C126550C7BB942E4309C7B5A6768C180417D0A5EE5C7B96A1BEFC94836A05C01FA873DEAEF39AEFA78181F424D35DC1F3264BB5EDC9FC85C63466B02763EF1168ECC0CD7F0F39079C2E8577310220D7936D1A4A6A1FE6B3755B09FBCBF94BDD64C681EE61628876756EC241E19");
        var expectedClientPublicKey = Convert.FromHexString(
            "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000019");
        Assert.Equal(expectedCiphertext.Concat(expectedClientPublicKey), server.ReceivedBytes);
        Assert.Equal(2, random.FillCount);
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

    [Fact]
    public async Task Rejects_nonzero_odd_modulus_with_a_leading_zero_byte()
    {
        var modulus = new byte[64];
        modulus[1] = 0x80;
        modulus[^1] = 1;
        var serverPublic = ToFixedWidth(new BigInteger(2), modulus.Length);
        var challenge = ArdServerFixture.EncodeChallenge(5, 64, modulus, serverPublic);
        await using var server = ArdServerFixture.ForServerBytes(challenge);

        await Assert.ThrowsAsync<RfbProtocolException>(() => AuthenticateWithEmptyCredentialsAsync(server.ClientStream));
        Assert.Empty(server.ReceivedBytes);
    }

    [Fact]
    public async Task Accepts_key_length_at_512_byte_upper_bound()
    {
        var modulus = new byte[512];
        modulus[0] = 0x80;
        modulus[^1] = 1;
        var serverPublic = ToFixedWidth(new BigInteger(2), modulus.Length);
        var challenge = ArdServerFixture.EncodeChallenge(5, 512, modulus, serverPublic);
        var serverBytes = new List<byte>(challenge);
        serverBytes.AddRange([0, 0, 0, 0]);
        await using var server = ArdServerFixture.ForServerBytes(serverBytes.ToArray(), maxReadChunk: 17);

        await AuthenticateWithEmptyCredentialsAsync(server.ClientStream).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(128 + 512, server.ReceivedBytes.Length);
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
    public async Task Masks_partial_byte_unused_high_bits_before_private_exponent_sampling()
    {
        var modulus = new byte[64];
        modulus[0] = 0x1f;
        modulus[^1] = 1;
        var serverPublic = ToFixedWidth(new BigInteger(2), modulus.Length);
        var challenge = ArdServerFixture.EncodeChallenge(5, 64, modulus, serverPublic);
        var serverBytes = new List<byte>(challenge);
        serverBytes.AddRange([0, 0, 0, 0]);
        await using var server = ArdServerFixture.ForServerBytes(serverBytes.ToArray());
        var random = new PartialHighBitsRandomSource();
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
    public async Task Reads_failure_reason_for_apple_003_889()
    {
        await using var server = ArdServerFixture.Create(
            securityResult: 7,
            reasonBytes: Encoding.UTF8.GetBytes("Apple authentication denied"));
        using var username = SecretMaterial.FromUtf8("user");
        using var password = SecretMaterial.FromUtf8("password");

        var exception = await Assert.ThrowsAsync<ArdAuthenticationRejectedException>(() =>
            new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                server.ClientStream,
                RfbVersion.V3_889,
                username,
                password,
                CancellationToken.None));

        Assert.Equal(7u, exception.ResultCode);
        Assert.Equal("Apple authentication denied", exception.Reason);
        Assert.False(exception.IsReasonTruncated);
    }

    [Fact]
    public async Task Redacts_repeated_username_and_password_bytes_before_reason_decode_and_sanitize()
    {
        const string testUsername = "echo-user";
        const string testPassword = "echo-password";
        var reasonBytes = Encoding.UTF8.GetBytes(
            $"\u001b[31m {testUsername}/{testPassword} {testUsername}\n{testPassword}");
        await using var server = ArdServerFixture.Create(securityResult: 7, reasonBytes: reasonBytes);
        using var username = SecretMaterial.FromUtf8(testUsername);
        using var password = SecretMaterial.FromUtf8(testPassword);

        var exception = await Assert.ThrowsAsync<ArdAuthenticationRejectedException>(() =>
            new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                server.ClientStream,
                RfbVersion.V3_8,
                username,
                password,
                CancellationToken.None));

        const string expected = "\\u001B[31m [REDACTED]/[REDACTED] [REDACTED]\\u000A[REDACTED]";
        Assert.Equal(expected, exception.Reason);
        Assert.DoesNotContain(testUsername, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(testPassword, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(testUsername, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(testPassword, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CompositeCredentialLeakCases))]
    public async Task Discards_remote_reason_when_redaction_recomposes_a_complete_credential(
        string testUsername,
        string testPassword,
        string remoteReason)
    {
        await using var server = ArdServerFixture.Create(
            securityResult: 7,
            reasonBytes: Encoding.UTF8.GetBytes(remoteReason));
        using var username = SecretMaterial.FromUtf8(testUsername);
        using var password = SecretMaterial.FromUtf8(testPassword);

        var exception = await Assert.ThrowsAsync<ArdAuthenticationRejectedException>(() =>
            new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                server.ClientStream,
                RfbVersion.V3_8,
                username,
                password,
                CancellationToken.None));
        var probeOutput = ProbeOutput.FormatFailure(exception);
        var compositeCredential = testUsername.Contains("[REDACTED]", StringComparison.Ordinal)
            ? testUsername
            : testPassword;

        Assert.Equal(7u, exception.ResultCode);
        Assert.True(string.IsNullOrEmpty(exception.Reason));
        Assert.False(exception.IsReasonTruncated);
        Assert.DoesNotContain(compositeCredential, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(remoteReason, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(compositeCredential, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(remoteReason, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(compositeCredential, probeOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(remoteReason, probeOutput, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NonAsciiCredentialReasonCases))]
    public async Task Discards_remote_reason_without_decoding_when_either_credential_is_non_ascii(
        string testUsername,
        string testPassword,
        string remoteReason)
    {
        var reasonBytes = Encoding.UTF8.GetBytes(remoteReason);
        await using var server = ArdServerFixture.Create(securityResult: 23, reasonBytes: reasonBytes);
        using var username = SecretMaterial.FromUtf8(testUsername);
        using var password = SecretMaterial.FromUtf8(testPassword);

        var exception = await Assert.ThrowsAsync<ArdAuthenticationRejectedException>(() =>
            new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                server.ClientStream,
                RfbVersion.V3_8,
                username,
                password,
                CancellationToken.None));
        var probeOutput = ProbeOutput.FormatFailure(exception);
        var nonAsciiSecret = testUsername.Any(character => character > 0x7f)
            ? testUsername
            : testPassword;

        Assert.Equal(23u, exception.ResultCode);
        Assert.Equal(string.Empty, exception.Reason);
        Assert.False(exception.IsReasonTruncated);
        Assert.Equal(
            4 + (2 * server.KeyLength) + sizeof(uint) + sizeof(uint) + reasonBytes.Length,
            server.ServerBytesRead);
        Assert.DoesNotContain(remoteReason, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(remoteReason, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(remoteReason, probeOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(nonAsciiSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nonAsciiSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(nonAsciiSecret, probeOutput, StringComparison.Ordinal);
        Assert.Equal("Apple Remote Desktop authentication was rejected (result 23).", exception.Message);
    }

    [Fact]
    public async Task Discards_remote_reason_when_invalid_utf8_replacement_recomposes_a_credential()
    {
        await using var server = ArdServerFixture.Create(
            securityResult: 7,
            reasonBytes: [0xc3, (byte)'X', (byte)'(']);
        using var username = SecretMaterial.FromUtf8("X");
        using var password = SecretMaterial.FromUtf8("�[REDACTED](");

        var exception = await Assert.ThrowsAsync<ArdAuthenticationRejectedException>(() =>
            new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                server.ClientStream,
                RfbVersion.V3_8,
                username,
                password,
                CancellationToken.None));

        Assert.Equal(string.Empty, exception.Reason);
        Assert.Equal("Apple Remote Desktop authentication was rejected (result 7).", exception.Message);
    }

    [Fact]
    public async Task Composite_fail_closed_path_does_not_materialize_a_reason_string()
    {
        await using var server = ArdServerFixture.Create(
            securityResult: 7,
            reasonBytes: Encoding.UTF8.GetBytes("abcXdef"));
        using var username = SecretMaterial.FromUtf8("X");
        using var password = SecretMaterial.FromUtf8("abc[REDACTED]def");
        var materializationCount = 0;
        RfbFailureReasonReader.MaterializationObserver = () => materializationCount++;

        try
        {
            var exception = await Assert.ThrowsAsync<ArdAuthenticationRejectedException>(() =>
                new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                    server.ClientStream,
                    RfbVersion.V3_8,
                    username,
                    password,
                    CancellationToken.None));

            Assert.Equal(string.Empty, exception.Reason);
            Assert.Equal(0, materializationCount);
        }
        finally
        {
            RfbFailureReasonReader.MaterializationObserver = null;
        }
    }

    [Fact]
    public async Task Preserves_result_code_when_rfb_38_reason_length_exceeds_limit()
    {
        const string testPassword = "limit-secret-password";
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(testPassword, 8)));
        await using var server = ArdServerFixture.Create(securityResult: 7, reasonBytes: payload);
        using var username = SecretMaterial.FromUtf8("limit-user");
        using var password = SecretMaterial.FromUtf8(testPassword);

        var exception = await Assert.ThrowsAsync<ArdAuthenticationRejectedException>(() =>
            new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                server.ClientStream,
                RfbVersion.V3_8,
                username,
                password,
                new ProtocolLimits(128, 1024),
                CancellationToken.None));

        Assert.Equal(7u, exception.ResultCode);
        Assert.IsType<RfbProtocolException>(exception.InnerException);
        Assert.DoesNotContain(testPassword, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preserves_result_code_when_rfb_38_reason_payload_ends_early()
    {
        const string testUsername = "eof-secret-user";
        var payload = Encoding.UTF8.GetBytes(testUsername);
        await using var server = ArdServerFixture.Create(
            securityResult: 7,
            reasonBytes: payload,
            declaredReasonLength: checked((uint)payload.Length + 5));
        using var username = SecretMaterial.FromUtf8(testUsername);
        using var password = SecretMaterial.FromUtf8("eof-password");

        var exception = await Assert.ThrowsAsync<ArdAuthenticationRejectedException>(() =>
            new ArdAuthenticator(new PrivateExponentTwoRandomSource()).AuthenticateAsync(
                server.ClientStream,
                RfbVersion.V3_8,
                username,
                password,
                CancellationToken.None));

        Assert.Equal(7u, exception.ResultCode);
        Assert.IsType<RfbProtocolException>(exception.InnerException);
        Assert.DoesNotContain(testUsername, exception.ToString(), StringComparison.Ordinal);
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
        Assert.Equal(4096, exception.Reason!.Length);
        Assert.Equal(2048, exception.Reason.EnumerateRunes().Count());
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
    public async Task Ard_server_fixture_does_not_release_security_result_before_complete_response()
    {
        await using var server = ArdServerFixture.Create();
        var reader = new RfbReader(server.ClientStream, ProtocolLimits.Default);
        _ = await reader.ReadBytesAsync(4 + (2 * server.KeyLength), CancellationToken.None);

        var prematureResultRead = reader.ReadUInt32Async(CancellationToken.None).AsTask();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            prematureResultRead.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(server.ReceivedBytes);
    }

    [Fact]
    public async Task Disposing_ard_server_fixture_releases_pending_result_gate_without_faulting_the_gate()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var server = ArdServerFixture.Create();
            var reader = new RfbReader(server.ClientStream, ProtocolLimits.Default);
            _ = await reader.ReadBytesAsync(4 + (2 * server.KeyLength), CancellationToken.None);
            var pendingResultRead = reader.ReadUInt32Async(CancellationToken.None).AsTask();

            await server.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                pendingResultRead.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Staged_ard_fixture_reliably_requires_the_full_response_before_result()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var server = ArdServerFixture.Create(maxReadChunk: 1);

            await AuthenticateWithEmptyCredentialsAsync(server.ClientStream);

            Assert.Equal(128 + server.KeyLength, server.ReceivedBytes.Length);
            Assert.Equal(4 + (2 * server.KeyLength) + sizeof(uint), server.ServerBytesRead);
        }
    }

    [Fact]
    public async Task Ard_server_fixture_rejects_a_response_larger_than_the_negotiated_length()
    {
        await using var server = ArdServerFixture.Create();
        var reader = new RfbReader(server.ClientStream, ProtocolLimits.Default);
        var writer = new RfbWriter(server.ClientStream);
        _ = await reader.ReadBytesAsync(4 + (2 * server.KeyLength), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.WriteBytesAsync(new byte[129 + server.KeyLength], CancellationToken.None));
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

    public static TheoryData<string, string, string> CompositeCredentialLeakCases =>
        new()
        {
            { "X", "abc[REDACTED]def", "abcXdef" },
            { "abc[REDACTED]def", "X", "abcXdef" },
            { "aa", "[REDACTED][REDACTED]a", "aaaaa" },
            { "X", "a[REDACTED]b", "aXb" },
            { "X", "\\u001B[REDACTED]", "\u001bX" },
        };

    public static TheoryData<string, string, string> NonAsciiCredentialReasonCases =>
        new()
        {
            { "user", "é", "Denied e\u0301" },
            { "user", "e\u0301", "Denied é" },
            { "user", "a\u0301\u0327", "Denied á\u0327" },
            { "usér", "password", "Denied use\u0301r" },
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

    private static byte[] SuccessfulChallengeAndResult()
    {
        var modulus = Convert.FromHexString(FixtureModulusHex);
        var serverPublicKey = Convert.FromHexString(
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007D");
        return [.. ArdServerFixture.EncodeChallenge(5, 64, modulus, serverPublicKey), 0, 0, 0, 0];
    }

    private class ScriptedDuplexStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingFirstWriteDuplexStream(byte[] input) : ScriptedDuplexStream(input)
    {
        private readonly TaskCompletionSource _writeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;

        public Task WriteStarted => _writeStarted.Task;
        public List<int> WriteLengths { get; } = [];
        public List<byte> Bytes { get; } = [];

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) == 1)
            {
                _writeStarted.TrySetResult();
                await _releaseWrite.Task.WaitAsync(cancellationToken);
            }

            WriteLengths.Add(buffer.Length);
            Bytes.AddRange(buffer.ToArray());
        }

        public void ReleaseWrite() => _releaseWrite.TrySetResult();
    }

    private sealed class CapturingFailureDuplexStream(byte[] input) : ScriptedDuplexStream(input)
    {
        public byte[]? CapturedBuffer { get; private set; }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(MemoryMarshal.TryGetArray(buffer, out var segment));
            CapturedBuffer = segment.Array;
            throw new IOException("Injected ARD response write failure.");
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

    private sealed class PartialHighBitsRandomSource : IRandomSource
    {
        public int FillCount { get; private set; }

        public void Fill(Span<byte> destination)
        {
            FillCount++;
            destination.Clear();
            destination[0] = 0xe0;
        }
    }

    private sealed class KnownAnswerRandomSource : IRandomSource
    {
        public int FillCount { get; private set; }

        public void Fill(Span<byte> destination)
        {
            if (FillCount++ == 0)
            {
                destination.Clear();
                return;
            }

            for (var index = 0; index < destination.Length; index++)
            {
                destination[index] = checked((byte)index);
            }
        }
    }
}
