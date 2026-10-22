using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Core.Tests.Providers;

public sealed class CodexSignInCommandTests
{
    private const string NpmNative =
        @"C:\Users\sam\AppData\Roaming\npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";

    [Fact]
    public void Calls_the_resolved_binary_instead_of_the_bare_name()
    {
        // A bare "codex" makes PowerShell pick the npm codex.ps1 shim, which the
        // default Restricted execution policy blocks.
        var command = CodexSignInCommand.Create(@"C:\Users\sam\.codex", "Sign in.", NpmNative);

        Assert.Equal(
            @"$env:CODEX_HOME='C:\Users\sam\.codex'; Write-Host 'Sign in.' -ForegroundColor Cyan; & '" + NpmNative + "' login",
            command);
    }

    [Fact]
    public void Quotes_every_inserted_value()
    {
        var command = CodexSignInCommand.Create(@"C:\it's", "Don't wait.", @"C:\o'neil\codex.exe");

        Assert.Equal(
            @"$env:CODEX_HOME='C:\it''s'; Write-Host 'Don''t wait.' -ForegroundColor Cyan; & 'C:\o''neil\codex.exe' login",
            command);
    }
}
