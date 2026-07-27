using System.Text;
using System.Text.Json;
using WinARD.Infrastructure.Diagnostics;
using Xunit;

namespace WinARD.Infrastructure.Tests;

public sealed class SecretRedactorTests
{
    [Fact]
    public void RedactsRegisteredCredentialVariantsWithoutRetainingStrings()
    {
        using var redactor = new SecretRedactor();
        var secrets = new[]
        {
            "mac-P@ss word/1",
            "key-passphrase!",
            "vault-master-phrase",
            "ssh-password-value",
        };
        var registrations = secrets
            .Select(secret => redactor.Register(Encoding.UTF8.GetBytes(secret)))
            .ToArray();

        try
        {
            var text = string.Join('|', secrets.SelectMany(secret => new[]
            {
                secret,
                Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)),
                Uri.EscapeDataString(secret),
                LowerPercentHex(Uri.EscapeDataString(secret)),
            }));

            var result = redactor.Redact(text);

            Assert.Contains(SecretRedactor.RedactedValue, result, StringComparison.Ordinal);
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)),
                    result,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(Uri.EscapeDataString(secret), result, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    LowerPercentHex(Uri.EscapeDataString(secret)),
                    result,
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        }
    }

    [Fact]
    public void LongestVariantWinsAndRegistrationCanBeRemoved()
    {
        using var redactor = new SecretRedactor();
        using var shorter = redactor.Register("alpha".AsSpan());
        var longer = redactor.Register("alpha-beta".AsSpan());

        Assert.Equal("x=[REDACTED]", redactor.Redact("x=alpha-beta"));

        longer.Dispose();
        Assert.Equal("x=[REDACTED]-beta", redactor.Redact("x=alpha-beta"));
    }

    [Fact]
    public void EmptyAndShortSecretsAreIgnoredToAvoidOverRedaction()
    {
        using var redactor = new SecretRedactor();
        using var empty = redactor.Register(ReadOnlySpan<char>.Empty);
        using var shortSecret = redactor.Register("abc".AsSpan());

        Assert.Equal("abc is ordinary text", redactor.Redact("abc is ordinary text"));
    }

    [Fact]
    public void RedactsSystemTextJsonUnicodeEscapesIncludingSurrogatePairs()
    {
        using var redactor = new SecretRedactor();
        const string secret = "密碼🔐";
        using var registration = redactor.Register(secret.AsSpan());
        var escaped = JsonSerializer.Serialize(secret);
        var lowerHex = LowerUnicodeHex(escaped);

        var result = redactor.Redact($"upper={escaped};lower={lowerHex}");

        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.DoesNotContain(escaped[1..^1], result, StringComparison.Ordinal);
        Assert.DoesNotContain(lowerHex[1..^1], result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("VaultMaster")]
    [InlineData("vault-master")]
    [InlineData("vault_master")]
    [InlineData("MacPassword")]
    [InlineData("SshPassword")]
    [InlineData("PrivateKeyPassphrase")]
    public void StructuredSinkRedactsNormalizedSecretFieldNamesWithoutRegistration(string name)
    {
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);

        sink.Write(new SafeDiagnosticEventInput(
            "FIELD_TEST",
            "corr-field",
            "safe",
            [new(name, "unregistered-sensitive-value")]));

        Assert.Equal(
            SecretRedactor.RedactedValue,
            sink.Snapshot().Single().Fields.Single(field => field.Name == name).Value);
    }

    [Fact]
    public void VaultMasterCategoryIsAlwaysRedactedWithoutRegistration()
    {
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor);

        sink.Write(new SafeDiagnosticEventInput(
            "FIELD_TEST",
            "corr-field-kind",
            "safe",
            [new("master", "unregistered-master", DiagnosticFieldCategory.VaultMaster)]));

        Assert.Equal(
            SecretRedactor.RedactedValue,
            sink.Snapshot().Single().Fields.Single(field => field.Name == "master").Value);
    }

    [Fact]
    public async Task RegistrationAndRedactionAreThreadSafe()
    {
        using var redactor = new SecretRedactor();
        var tasks = Enumerable.Range(0, 32).Select(async index =>
        {
            var secret = $"parallel-secret-{index:D2}";
            using var registration = redactor.Register(secret.AsSpan());
            for (var iteration = 0; iteration < 50; iteration++)
            {
                var result = redactor.Redact($"value={secret}");
                Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
                await Task.Yield();
            }
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void ClearAndDisposeRemoveAndZeroRegistrations()
    {
        var redactor = new SecretRedactor();
        using var registration = redactor.Register("clear-me-secret".AsSpan());
        Assert.True(redactor.RegisteredVariantCount > 0);

        redactor.Clear();

        Assert.Equal(0, redactor.RegisteredVariantCount);
        Assert.Equal("clear-me-secret", redactor.Redact("clear-me-secret"));
        redactor.Dispose();
        Assert.Throws<ObjectDisposedException>(() => redactor.Redact("anything"));
    }

    [Fact]
    public void StructuredSinkRedactsSensitiveCategoriesBeforeRingStorage()
    {
        using var redactor = new SecretRedactor();
        using var registration = redactor.Register("query-secret-value".AsSpan());
        var sink = new InMemorySafeDiagnosticSink(redactor, capacity: 2, maxFieldLength: 128);

        sink.Write(new SafeDiagnosticEventInput(
            "CONNECT_FAILED",
            "corr-1",
            "failed",
            [
                new("ClipboardContent", "private text", DiagnosticFieldCategory.ClipboardContent),
                new("Password", "password value", DiagnosticFieldCategory.Password),
                new("endpoint", "https://host/?token=query-secret-value"),
            ],
            new InvalidOperationException(
                "json=\"query-secret-value\" base64=" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes("query-secret-value")))));
        registration.Dispose();
        sink.Write(new SafeDiagnosticEventInput("SECOND", "corr-2", "second"));
        sink.Write(new SafeDiagnosticEventInput("THIRD", "corr-3", "third"));

        var events = sink.Snapshot();
        var serialized = string.Join('\n', events.Select(item => item.ToString()));
        Assert.Equal(2, events.Count);
        Assert.DoesNotContain("corr-1", serialized, StringComparison.Ordinal);

        sink.Write(new SafeDiagnosticEventInput(
            "FOURTH",
            "corr-4",
            "clipboard=private text",
            [new("clipboard", "private text", DiagnosticFieldCategory.ClipboardContent)]));
        var latest = sink.Snapshot()[^1];
        Assert.DoesNotContain("private text", latest.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            SecretRedactor.RedactedValue,
            latest.Fields.Single(field => field.Name == "clipboard").Value);
    }

    [Fact]
    public async Task StructuredSinkSupportsConcurrentBoundedWritesAndSnapshots()
    {
        const int capacity = 64;
        using var redactor = new SecretRedactor();
        var sink = new InMemorySafeDiagnosticSink(redactor, capacity, 128);
        var writers = Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            for (var index = 0; index < 250; index++)
            {
                sink.Write(new SafeDiagnosticEventInput(
                    "PARALLEL",
                    $"writer-{writer}-event-{index}",
                    "safe"));
                Assert.InRange(sink.Snapshot().Count, 0, capacity);
            }
        }));

        await Task.WhenAll(writers);

        var snapshot = sink.Snapshot();
        Assert.Equal(capacity, snapshot.Count);
        Assert.All(snapshot, item => Assert.Equal("PARALLEL", item.Code));
    }

    [Fact]
    public void SafeDiagnosticWriterNeverPropagatesSinkFailure()
    {
        ISafeDiagnosticSink sink = new ThrowingSink();

        var written = sink.TryWrite(new SafeDiagnosticEventInput("CODE", "corr", "safe"));

        Assert.False(written);
    }

    [Fact]
    public void SecretRegistrationRejectsOversizeAndEnforcesRegistrationAndVariantLimits()
    {
        using var redactor = new SecretRedactor(new SecretRedactorLimits(
            MaxSecretBytes: 16,
            MaxRegisteredSecrets: 2,
            MaxVariantsPerSecret: 3,
            MaxTextUtf8Bytes: 128));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            redactor.Register(new string('x', 17).AsSpan()));
        using var first = redactor.Register("first-secret".AsSpan());
        using var second = redactor.Register("second-secret".AsSpan());
        Assert.Throws<InvalidOperationException>(() => redactor.Register("third-secret".AsSpan()));
        Assert.Equal(2, redactor.RegisteredSecretCount);
        Assert.InRange(redactor.RegisteredVariantCount, 2, 6);

        first.Dispose();
        using var replacement = redactor.Register("third-secret".AsSpan());
        Assert.Equal(2, redactor.RegisteredSecretCount);
    }

    [Fact]
    public void OversizeCharRegistrationHasLengthGuardBeforeUtf8ByteCounting()
    {
        var source = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Infrastructure", "Diagnostics", "SecretRedactor.cs"));
        var method = source.IndexOf(
            "public IDisposable Register(ReadOnlySpan<char> secret)",
            StringComparison.Ordinal);
        var nextMethod = source.IndexOf(
            "public IDisposable Register(ReadOnlySpan<byte> secret)",
            method,
            StringComparison.Ordinal);
        var lengthGuard = source.IndexOf(
            "secret.Length > _limits.MaxSecretBytes",
            method,
            StringComparison.Ordinal);
        var byteCount = source.IndexOf(
            "Encoding.UTF8.GetByteCount(secret)",
            method,
            StringComparison.Ordinal);

        Assert.True(method >= 0);
        Assert.True(nextMethod > method);
        Assert.InRange(lengthGuard, method, byteCount - 1);
        Assert.InRange(byteCount, lengthGuard + 1, nextMethod - 1);
    }

    [Fact]
    public void RedactionBoundsUtf8InputAtRuneBoundary()
    {
        using var redactor = new SecretRedactor(new SecretRedactorLimits(
            MaxSecretBytes: 32,
            MaxRegisteredSecrets: 2,
            MaxVariantsPerSecret: 7,
            MaxTextUtf8Bytes: 7));
        using var registration = redactor.Register("secret-value".AsSpan());

        var result = redactor.Redact("密密密secret-value");

        Assert.Equal(SecretRedactor.RedactedValue[..7], result);
        Assert.True(Encoding.UTF8.GetByteCount(result) <= 7);
        Assert.DoesNotContain('\uFFFD', result);
    }

    [Fact]
    public void OversizeDirectRedactionDoesNotExposeASecretPrefixAcrossTheBoundary()
    {
        const int boundary = 64 * 1_024;
        const string secret = "boundary-secret-value";
        using var redactor = new SecretRedactor();
        using var registration = redactor.Register(secret.AsSpan());

        var result = redactor.Redact(
            new string('x', boundary - 8) + secret + "-untrusted-tail");

        Assert.Equal(SecretRedactor.RedactedValue, result);
        Assert.DoesNotContain(secret[..8], result, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizeDirectRedactionFailsClosedWhenMultibyteSecretCrossesUtf8Boundary()
    {
        const int boundary = 64 * 1_024;
        const string secret = "密碼🔐boundary-secret";
        using var redactor = new SecretRedactor();
        using var registration = redactor.Register(secret.AsSpan());
        var secretBytes = Encoding.UTF8.GetByteCount(secret);
        var prefixLength = boundary - (secretBytes / 2);

        var result = redactor.Redact(
            new string('x', prefixLength) + secret + "-untrusted-tail");

        Assert.Equal(SecretRedactor.RedactedValue, result);
        Assert.DoesNotContain(secret[..2], result, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizeDiagnosticMessageDoesNotExposeASecretPrefixAcrossTheBoundary()
    {
        const int boundary = 64 * 1_024;
        const string secret = "boundary-secret-value";
        using var redactor = new SecretRedactor();
        using var registration = redactor.Register(secret.AsSpan());
        var sink = new InMemorySafeDiagnosticSink(redactor, new SafeDiagnosticLimits(
            MaxEvents: 2,
            MaxRawTextUtf8Bytes: boundary,
            MaxFieldsPerEvent: 2,
            MaxFieldUtf8Bytes: boundary,
            MaxRingApproximateBytes: boundary * 2));

        sink.Write(new SafeDiagnosticEventInput(
            "OVERSIZE_MESSAGE",
            "corr-message",
            new string('x', boundary - 8) + secret + "-untrusted-tail"));

        var message = sink.Snapshot().Single().Message;
        Assert.Equal(SecretRedactor.RedactedValue, message);
        Assert.DoesNotContain(secret[..8], message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizeDiagnosticFieldDoesNotExposeASecretPrefixAcrossTheBoundary()
    {
        const int boundary = 64 * 1_024;
        const string secret = "boundary-secret-value";
        using var redactor = new SecretRedactor();
        using var registration = redactor.Register(secret.AsSpan());
        var sink = new InMemorySafeDiagnosticSink(redactor, new SafeDiagnosticLimits(
            MaxEvents: 2,
            MaxRawTextUtf8Bytes: boundary,
            MaxFieldsPerEvent: 2,
            MaxFieldUtf8Bytes: boundary,
            MaxRingApproximateBytes: boundary * 2));

        sink.Write(new SafeDiagnosticEventInput(
            "OVERSIZE_FIELD",
            "corr-field",
            "safe",
            [new DiagnosticField(
                "detail",
                new string('x', boundary - 8) + secret + "-untrusted-tail")]));

        var value = sink.Snapshot().Single().Fields.Single().Value;
        Assert.Equal(SecretRedactor.RedactedValue, value);
        Assert.DoesNotContain(secret[..8], value, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredSinkBoundsRawTextFieldsAndApproximateRingBytes()
    {
        using var redactor = new SecretRedactor();
        var limits = new SafeDiagnosticLimits(
            MaxEvents: 100,
            MaxRawTextUtf8Bytes: 13,
            MaxFieldsPerEvent: 2,
            MaxFieldUtf8Bytes: 7,
            MaxRingApproximateBytes: 160);
        var sink = new InMemorySafeDiagnosticSink(redactor, limits);
        for (var index = 0; index < 20; index++)
        {
            sink.Write(new SafeDiagnosticEventInput(
                "EVENT",
                $"corr-{index}",
                "密密密密密密password=tail-secret",
                Enumerable.Range(0, 8)
                    .Select(field => new DiagnosticField($"field-{field}", "密密密密"))
                    .ToArray()));
        }

        var snapshot = sink.Snapshot();
        Assert.NotEmpty(snapshot);
        Assert.True(snapshot.Count < 20);
        Assert.True(sink.ApproximateSizeBytes <= limits.MaxRingApproximateBytes);
        Assert.All(snapshot, item =>
        {
            Assert.True(Encoding.UTF8.GetByteCount(item.Message) <= limits.MaxRawTextUtf8Bytes);
            Assert.DoesNotContain('\uFFFD', item.Message);
            Assert.Equal(limits.MaxFieldsPerEvent, item.Fields.Count);
            Assert.All(item.Fields, field =>
            {
                Assert.True(Encoding.UTF8.GetByteCount(field.Name) <= limits.MaxFieldUtf8Bytes);
                Assert.True(Encoding.UTF8.GetByteCount(field.Value) <= limits.MaxFieldUtf8Bytes);
                Assert.DoesNotContain('\uFFFD', field.Value);
            });
        });
    }

    [Fact]
    public async Task ConcurrentSnapshotRedactionAndUnregistrationStayBoundedAndSafe()
    {
        var limits = new SecretRedactorLimits(
            MaxSecretBytes: 64,
            MaxRegisteredSecrets: 16,
            MaxVariantsPerSecret: 7,
            MaxTextUtf8Bytes: 4_096);
        using var redactor = new SecretRedactor(limits);
        var registrations = Enumerable.Range(0, limits.MaxRegisteredSecrets)
            .Select(index => redactor.Register($"bounded-secret-{index:D2}".AsSpan()))
            .ToArray();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try
            {
                for (var iteration = 0; iteration < 250; iteration++)
                {
                    var result = redactor.Redact(
                        new string('x', 3_000) + " bounded-secret-07 " + new string('y', 3_000));
                    Assert.True(Encoding.UTF8.GetByteCount(result) <= limits.MaxTextUtf8Bytes);
                }
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        })).ToArray();
        var unregister = Task.Run(() =>
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        });

        await Task.WhenAll([.. readers, unregister]);

        Assert.Empty(failures);
        Assert.Equal(0, redactor.RegisteredSecretCount);
        Assert.Equal(0, redactor.RegisteredVariantCount);
    }

    private static string LowerPercentHex(string value)
    {
        var characters = value.ToCharArray();
        for (var index = 0; index + 2 < characters.Length; index++)
        {
            if (characters[index] != '%')
            {
                continue;
            }

            characters[index + 1] = char.ToLowerInvariant(characters[index + 1]);
            characters[index + 2] = char.ToLowerInvariant(characters[index + 2]);
            index += 2;
        }

        return new string(characters);
    }

    private static string RepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinARD.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException("Could not locate repository root.")
            : Path.Combine([directory.FullName, .. segments]);
    }

    private sealed class ThrowingSink : ISafeDiagnosticSink
    {
        public void Write(SafeDiagnosticEventInput diagnosticEvent) =>
            throw new InvalidOperationException("sink failed");

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => [];
    }

    private static string LowerUnicodeHex(string value)
    {
        var characters = value.ToCharArray();
        for (var index = 0; index + 5 < characters.Length; index++)
        {
            if (characters[index] != '\\' || characters[index + 1] != 'u')
            {
                continue;
            }

            for (var hex = index + 2; hex < index + 6; hex++)
            {
                characters[hex] = char.ToLowerInvariant(characters[hex]);
            }

            index += 5;
        }

        return new string(characters);
    }
}
