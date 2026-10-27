using System.Text.RegularExpressions;

namespace DroidDeck.Core.Diagnostics;

/// <summary>
/// Removes secret-looking values from log lines and diagnostics reports. The
/// redactor is deliberately conservative: it prefers hiding a harmless value
/// over leaking a credential.
/// </summary>
public static partial class Redactor
{
    /// <summary>Gets the replacement text used for hidden values.</summary>
    public const string Mask = "<redacted>";

    /// <summary>Returns the text with credentials, tokens, and keys masked.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var value = text;
        value = PrivateKeyBlockRegex().Replace(value, "<redacted-private-key>");
        value = UrlUserInfoRegex().Replace(value, "$1<redacted>@");
        value = BearerRegex().Replace(value, "Bearer <redacted>");
        value = JwtRegex().Replace(value, "<redacted-jwt>");
        value = KnownTokenRegex().Replace(value, Mask);
        value = SecretArgumentRegex().Replace(value, "$1<redacted>");
        value = AuthorizationHeaderRegex().Replace(value, "Authorization: <redacted>");
        value = SecretAssignmentRegex().Replace(value, "$1<redacted>");
        return value;
    }

    [GeneratedRegex(
        @"\bAuthorization\s*[:=]\s*(?:(?:Bearer|Basic|Digest|Token)\s+)?\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlockRegex();

    [GeneratedRegex(
        @"\b(https?://)[^/@\s:]+:[^/@\s]+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfoRegex();

    [GeneratedRegex(
        @"\bBearer\s+[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(
        @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(
        @"\b(?:sk-[A-Za-z0-9]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|AIza[0-9A-Za-z_-]{30,}|xox[baprs]-[A-Za-z0-9-]{10,})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenRegex();

    [GeneratedRegex(
        @"(--(?:password|passwd|token|secret|api-key|apikey)\s+)(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretArgumentRegex();

    [GeneratedRegex(
        @"([""']?\b(?:password|passwd|pwd|secret|client[_-]?secret|access[_-]?token|refresh[_-]?token|token|api[_-]?key|apikey|credentials?|private[_-]?key)\b[""']?\s*[:=]\s*)(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();
}
