using System.IO;

namespace costats.Core.Shell;

/// <summary>
/// Resolves Windows system executables to their absolute System32 path and
/// exposes a trusted working directory for child processes.
/// </summary>
/// <remarks>
/// Launching a bare image name such as "powershell.exe" or "where.exe" through
/// CreateProcess lets an attacker-writable current directory, or an earlier
/// writable PATH entry, override even System32. Passing the absolute System32
/// path removes the image search entirely, so no such directory can supply the
/// binary. Pinning the child working directory to a rooted, app-controlled
/// folder additionally stops tools that search their own current directory
/// (for example "where.exe") from trusting a poisoned directory, and keeps the
/// child from inheriting one.
/// </remarks>
public static class SystemExecutables
{
    /// <summary>
    /// Absolute path to the Windows PowerShell 5.1 host. It lives in the
    /// "WindowsPowerShell\v1.0" folder under System32, not System32 itself.
    /// </summary>
    public static string PowerShell => ResolveWindowsPowerShell();

    /// <summary>Absolute System32 path to the command interpreter.</summary>
    public static string Cmd => ResolveSystem32("cmd.exe");

    /// <summary>Absolute System32 path to the PATH search tool.</summary>
    public static string Where => ResolveSystem32("where.exe");

    /// <summary>
    /// A rooted, app-controlled directory safe to use as a child process
    /// working directory, so no attacker-writable current directory is inherited
    /// or searched.
    /// </summary>
    public static string TrustedWorkingDirectory => AppContext.BaseDirectory;

    /// <summary>
    /// Returns the absolute System32 path for <paramref name="executableName"/>
    /// (for example "powershell.exe"). The ".exe" extension is appended when it
    /// is missing. On non-Windows platforms the bare name is returned unchanged;
    /// on Windows, if the system directory or the file cannot be resolved, the
    /// ".exe"-qualified bare name is returned. This keeps existing behavior and
    /// cross-platform tests working while removing the image search wherever the
    /// System32 copy is present.
    /// </summary>
    public static string ResolveSystem32(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        // Off Windows there is no System32; keep the caller's bare name so the
        // existing "which"/PATH behavior and cross-platform tests are preserved.
        if (!OperatingSystem.IsWindows())
        {
            return executableName;
        }

        var name = EnsureExeExtension(executableName);

        var systemDirectory = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(systemDirectory))
        {
            return name;
        }

        var qualified = Path.Combine(systemDirectory, name);
        return File.Exists(qualified) ? qualified : name;
    }

    private static string ResolveWindowsPowerShell()
    {
        const string leaf = "powershell.exe";

        if (!OperatingSystem.IsWindows())
        {
            return leaf;
        }

        var systemDirectory = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(systemDirectory))
        {
            return leaf;
        }

        var qualified = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", leaf);
        return File.Exists(qualified) ? qualified : leaf;
    }

    private static string EnsureExeExtension(string executableName)
        => executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executableName
            : executableName + ".exe";
}
