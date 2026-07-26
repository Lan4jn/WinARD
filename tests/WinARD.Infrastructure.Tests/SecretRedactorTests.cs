using System.Text;
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
        Assert.Equal(SecretRedactor.RedactedValue, latest.Fields["clipboard"]);
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
}
