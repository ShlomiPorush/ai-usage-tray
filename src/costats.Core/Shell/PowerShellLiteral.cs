using System.Text;

namespace costats.Core.Shell;

/// <summary>
/// Builds PowerShell single-quoted string literals.
/// </summary>
public static class PowerShellLiteral
{
    /// <summary>
    /// PowerShell treats four Unicode quotation marks as synonyms of the ASCII
    /// apostrophe, so all five close a single-quoted literal and all five must
    /// be doubled to stay inside it. Doubling only the apostrophe lets a path
    /// that contains one of the others end the literal and start a new
    /// statement. Written as code points so the file stays plain ASCII.
    /// </summary>
    private static readonly char[] ClosingQuotes =
    [
        '\'',         // U+0027 APOSTROPHE
        (char)0x2018, // LEFT SINGLE QUOTATION MARK
        (char)0x2019, // RIGHT SINGLE QUOTATION MARK
        (char)0x201A, // SINGLE LOW-9 QUOTATION MARK
        (char)0x201B  // SINGLE HIGH-REVERSED-9 QUOTATION MARK
    ];

    /// <summary>
    /// Wraps <paramref name="value"/> in single quotes so PowerShell reads it
    /// as one verbatim string, whatever it contains.
    /// </summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('\'');
        foreach (var character in value)
        {
            builder.Append(character);
            if (IsClosingQuote(character))
            {
                builder.Append(character);
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }

    /// <summary>True for a character that ends a PowerShell single-quoted literal.</summary>
    public static bool IsClosingQuote(char character) => Array.IndexOf(ClosingQuotes, character) >= 0;
}
