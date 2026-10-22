namespace costats.Core.Shell;

/// <summary>
/// Wraps a sign-in command for a visible PowerShell window that closes by
/// itself when the command succeeds and stays open on failure.
///
/// A failure is a command that cannot start (not found, or a script blocked
/// by the execution policy) or a native program that exits with a non-zero
/// code. The window then shows the reason and waits for Enter, so the user
/// can read it. Callers start PowerShell without -NoExit.
/// </summary>
public static class PowerShellSignInScript
{
    public static string PauseOnFailure(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        return "try { " + command + "; " +
            "if ($LASTEXITCODE) { throw \"exited with code $LASTEXITCODE\" } } " +
            "catch { Write-Host \"Sign-in did not finish: $_\" -ForegroundColor Red; " +
            "Read-Host 'Press Enter to close' | Out-Null }";
    }
}
