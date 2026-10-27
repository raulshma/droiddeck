using DroidDeck.Core.Diagnostics;

namespace DroidDeck.Core.Tests.Diagnostics;

public sealed class RedactorTests
{
    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("{\"apiKey\":\"abc123456\"}", "abc123456")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
    [InlineData("access_token = something-long", "something-long")]
    [InlineData("client_secret: topsecret", "topsecret")]
    public void Redact_MasksAssignedSecrets(string text, string secret)
    {
        var redacted = Redactor.Redact(text);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("<redacted>", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature", "eyJzdWIiOiIxMjM0NTY3ODkwIn0")]
    [InlineData("key sk-abcdefghijklmnopqrstuvwx", "sk-abcdefghijklmnopqrstuvwx")]
    [InlineData("key ghp_abcdefghijklmnopqrstuvwxyz0123", "ghp_abcdefghijklmnopqrstuvwxyz0123")]
    [InlineData("url https://user:password@example.com/path", "password")]
    [InlineData("tool --password hunter2", "hunter2")]
    public void Redact_MasksKnownTokenShapes(string text, string secret)
    {
        var redacted = Redactor.Redact(text);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_MasksPrivateKeyBlocks()
    {
        const string Text =
            """
            before
            -----BEGIN RSA PRIVATE KEY-----
            MIIEowIBAAKCAQEA
            -----END RSA PRIVATE KEY-----
            after
            """;

        var redacted = Redactor.Redact(Text);

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", redacted, StringComparison.Ordinal);
        Assert.Contains("<redacted-private-key>", redacted, StringComparison.Ordinal);
        Assert.Contains("before", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("The device is unauthorized. Run adb kill-server.")]
    [InlineData("A token bucket rate limiter is active.")]
    [InlineData("Metadata refreshed for emulator-5554.")]
    public void Redact_LeavesOrdinaryTextAlone(string text)
    {
        Assert.Equal(text, Redactor.Redact(text));
    }

    [Fact]
    public void Redact_HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, Redactor.Redact(null));
        Assert.Equal(string.Empty, Redactor.Redact(string.Empty));
    }
}
