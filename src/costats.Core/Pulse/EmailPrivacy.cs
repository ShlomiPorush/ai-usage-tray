namespace costats.Core.Pulse;

public static class EmailPrivacy
{
    private const char MaskCharacter = '\u2022';

    /// <summary>
    /// Keeps just enough shape to identify an address without making it useful
    /// in a screenshot. The full value is only returned after an explicit reveal.
    /// </summary>
    public static string Mask(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return string.Empty;
        }

        var normalized = email.Trim();
        var at = normalized.LastIndexOf('@');
        var dot = normalized.LastIndexOf('.');
        if (at <= 0 || dot <= at + 1 || dot == normalized.Length - 1)
        {
            return new string(MaskCharacter, Math.Clamp(normalized.Length, 6, 12));
        }

        return $"{normalized[0]}{new string(MaskCharacter, 5)}@{new string(MaskCharacter, 5)}{normalized[dot..]}";
    }
}
