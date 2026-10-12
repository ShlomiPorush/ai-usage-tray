using System.Text.RegularExpressions;

namespace costats.Application.Diagnostics;

/// <summary>
/// Last line of defence for text the app does not control (provider error
/// messages, exception messages) before it reaches a log. Log statements must
/// not pass secrets in the first place; this masks anything that still looks
/// like one: a long unbroken run of token characters (API keys, OAuth and JWT
/// tokens, the 32-character remote-view ids) and the value after an
/// Authorization-style keyword. It over-masks on purpose: a long identifier in
/// an error message is a small loss next to a leaked key.
/// </summary>
public static partial class LogRedaction
{
    public const string Marker = "[redacted]";
    public const int MaxLength = 300;

    // "Bearer <value>", and "<key>=<value>" or "<key>": "<value>" for keys
    // that name a credential. A bare "token expired" is left alone.
    [GeneratedRegex(@"(?i)\b(bearer)\s+[^\s,;""']+|\b(access_token|refresh_token|id_token|token|authorization|api[-_]?key|secret)[""']?\s*[:=]\s*[""']?(?:bearer\s+)?[^\s,;""'}]+")]
    private static partial Regex KeywordValue();

    // Dots and slashes are left out so type names and URL paths stay readable;
    // a JWT's segments and a token after "/u/" are each long enough anyway.
    [GeneratedRegex(@"[A-Za-z0-9_\-=+]{24,}")]
    private static partial Regex LongToken();

    /// <summary>Masks secret-looking parts of <paramref name="text"/> and caps its length.</summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = KeywordValue().Replace(
            text,
            match => match.Groups[1].Success
                ? match.Groups[1].Value + " " + Marker
                : match.Groups[2].Value + "=" + Marker);
        redacted = LongToken().Replace(redacted, Marker);
        return redacted.Length > MaxLength ? redacted[..MaxLength] + "..." : redacted;
    }
}
