using costats.Core.Shell;
using Xunit;

namespace costats.Core.Tests.Shell;

public sealed class PowerShellLiteralTests
{
    private const char RightSingleQuote = (char)0x2019;

    // PowerShell accepts all five of these as the closing quote of a
    // single-quoted string, so all five have to be doubled.
    [Theory]
    [InlineData('\'')]
    [InlineData((char)0x2018)]
    [InlineData((char)0x2019)]
    [InlineData((char)0x201A)]
    [InlineData((char)0x201B)]
    public void Every_closing_quote_character_is_doubled(char quote)
    {
        Assert.True(PowerShellLiteral.IsClosingQuote(quote));
        Assert.Equal($"'a{quote}{quote}b'", PowerShellLiteral.Quote($"a{quote}b"));
    }

    [Fact]
    public void An_injection_payload_stays_inside_one_literal()
    {
        // Checked during development with
        // [System.Management.Automation.Language.Parser]::ParseInput: the old
        // quoting made this command parse as five statements with no errors,
        // the new quoting makes it one assignment plus the intended command.
        var payload = "C:\\profiles\\a" + RightSingleQuote + "; Start-Process calc; $x=" + RightSingleQuote;

        var command = "$env:CODEX_HOME=" + PowerShellLiteral.Quote(payload) + "; codex login";

        Assert.Equal(
            "$env:CODEX_HOME='C:\\profiles\\a" + RightSingleQuote + RightSingleQuote +
            "; Start-Process calc; $x=" + RightSingleQuote + RightSingleQuote + "'; codex login",
            command);
    }

    [Theory]
    [InlineData("C:\\profiles\\alice\\.claude")]
    [InlineData("C:\\a b\\`$(whoami)\\\"quoted\"")]
    [InlineData("line\nbreak;semicolon")]
    public void Ordinary_paths_are_wrapped_without_further_escaping(string value) =>
        Assert.Equal($"'{value}'", PowerShellLiteral.Quote(value));

    [Fact]
    public void An_empty_value_is_still_a_valid_literal() =>
        Assert.Equal("''", PowerShellLiteral.Quote(""));
}
