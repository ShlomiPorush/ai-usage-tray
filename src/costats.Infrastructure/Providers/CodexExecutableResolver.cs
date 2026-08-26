namespace costats.Infrastructure.Providers;

internal static class CodexExecutableResolver
{
    public static string Resolve(string executable)
    {
        if (!executable.Equals("codex", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows())
        {
            return executable;
        }

        var standaloneInstallerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe");

        return File.Exists(standaloneInstallerPath) ? standaloneInstallerPath : executable;
    }
}
