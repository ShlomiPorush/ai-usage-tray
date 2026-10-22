using costats.Core.Shell;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Builds the PowerShell command that runs <c>codex login</c> for one Codex
/// profile folder in a visible window.
///
/// The bare name <c>codex</c> is not used: for an npm install, PowerShell
/// picks the <c>codex.ps1</c> shim, which Windows PowerShell refuses to run
/// under the default Restricted execution policy. The command calls the
/// binary that <see cref="CodexExecutableResolver"/> finds instead.
/// </summary>
public static class CodexSignInCommand
{
    public static string Create(string codexHome, string message) =>
        Create(codexHome, message, CodexExecutableResolver.Resolve(CodexExecutableResolver.DefaultName));

    internal static string Create(string codexHome, string message, string codexExecutable) =>
        $"$env:CODEX_HOME={PowerShellLiteral.Quote(codexHome)}; " +
        $"Write-Host {PowerShellLiteral.Quote(message)} -ForegroundColor Cyan; " +
        $"& {PowerShellLiteral.Quote(codexExecutable)} login";
}
