using costats.Core.Shell;
using Xunit;

namespace costats.Core.Tests.Shell;

public sealed class PowerShellSignInScriptTests
{
    // Checked during development in Windows PowerShell 5.1 under the
    // Restricted policy: a missing program, a blocked codex.ps1 and a non-zero
    // exit code all reach the catch block; exit code 0 closes the window.
    [Fact]
    public void Runs_the_command_and_pauses_only_on_failure() =>
        Assert.Equal(
            "try { codex login; if ($LASTEXITCODE) { throw \"exited with code $LASTEXITCODE\" } } " +
            "catch { Write-Host \"Sign-in did not finish: $_\" -ForegroundColor Red; " +
            "Read-Host 'Press Enter to close' | Out-Null }",
            PowerShellSignInScript.PauseOnFailure("codex login"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_command_is_rejected(string command) =>
        Assert.Throws<ArgumentException>(() => PowerShellSignInScript.PauseOnFailure(command));
}
