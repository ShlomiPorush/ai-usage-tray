using System.Runtime.InteropServices;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Finds the Codex CLI to start. Only file existence is checked; nothing
/// inside a Codex folder is read.
///
/// For the default name <c>codex</c> on Windows the first match wins, in this
/// fixed order:
/// <list type="number">
/// <item>The standalone installer: <c>%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe</c>.</item>
/// <item>The native <c>codex.exe</c> inside an npm global install of
/// <c>@openai/codex</c> under <c>%APPDATA%\npm</c> (see
/// <see cref="NpmNativeCandidates"/>). The real binary is started, not the
/// <c>codex.cmd</c> or <c>node</c> shim, so the app talks to one process it can
/// stop.</item>
/// <item>Each PATH folder in order, each PATHEXT extension in order: a
/// <c>codex.exe</c> there is used directly; an npm shim (<c>codex.cmd</c>,
/// <c>codex.bat</c>) is replaced by the native binary of the package next to
/// it, for npm prefixes other than the default, and used as is only when that
/// binary is missing.</item>
/// <item>Otherwise the bare name, which fails to start the same way it always
/// has.</item>
/// </list>
/// Any other name or a full path is returned unchanged.
/// </summary>
internal static class CodexExecutableResolver
{
    public const string DefaultName = "codex";

    public static string Resolve(string executable) => Resolve(executable, CodexResolverEnvironment.Current());

    public static string Resolve(string executable, CodexResolverEnvironment environment)
    {
        if (!executable.Equals(DefaultName, StringComparison.OrdinalIgnoreCase) || !environment.IsWindows)
        {
            return executable;
        }

        if (!string.IsNullOrWhiteSpace(environment.LocalApplicationData))
        {
            var standalone = Path.Combine(
                environment.LocalApplicationData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
            if (environment.FileExists(standalone))
            {
                return standalone;
            }
        }

        if (!string.IsNullOrWhiteSpace(environment.ApplicationData))
        {
            var npmNative = FindNpmNative(Path.Combine(environment.ApplicationData, "npm"), environment);
            if (npmNative is not null)
            {
                return npmNative;
            }
        }

        foreach (var directory in SplitList(environment.PathVariable))
        {
            foreach (var extension in Extensions(environment.PathExtVariable))
            {
                var candidate = Path.Combine(directory, DefaultName + extension.ToLowerInvariant());
                if (!environment.FileExists(candidate))
                {
                    continue;
                }

                if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    return FindNpmNative(directory, environment) ?? candidate;
                }
            }
        }

        return executable;
    }

    /// <summary>
    /// Where npm puts the native binary below an npm prefix (the folder that
    /// holds the <c>codex.cmd</c> shim), newest layout first: the platform
    /// package nested in <c>@openai/codex</c>, the same package hoisted next to
    /// it, and the single-package layout of older releases. The machine's own
    /// architecture comes first; the other one follows, because an x64 Node on
    /// an Arm64 PC installs the x64 package.
    /// </summary>
    internal static IEnumerable<string> NpmNativeCandidates(string npmPrefix, Architecture architecture)
    {
        (string Package, string Triple)[] platforms = architecture == Architecture.Arm64
            ? [("codex-win32-arm64", "aarch64-pc-windows-msvc"), ("codex-win32-x64", "x86_64-pc-windows-msvc")]
            : [("codex-win32-x64", "x86_64-pc-windows-msvc"), ("codex-win32-arm64", "aarch64-pc-windows-msvc")];
        var openai = Path.Combine(npmPrefix, "node_modules", "@openai");

        foreach (var (package, triple) in platforms)
        {
            yield return Path.Combine(openai, "codex", "node_modules", "@openai", package, "vendor", triple, "bin", "codex.exe");
            yield return Path.Combine(openai, package, "vendor", triple, "bin", "codex.exe");
            yield return Path.Combine(openai, "codex", "vendor", triple, "bin", "codex.exe");
            yield return Path.Combine(openai, "codex", "vendor", triple, "codex", "codex.exe");
        }
    }

    private static string? FindNpmNative(string npmPrefix, CodexResolverEnvironment environment) =>
        NpmNativeCandidates(npmPrefix, environment.Architecture).FirstOrDefault(environment.FileExists);

    private static IEnumerable<string> SplitList(string? value) =>
        (value ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim('"'))
            .Where(entry => entry.Length > 0 && Path.IsPathRooted(entry));

    private static IReadOnlyList<string> Extensions(string? pathExt)
    {
        var list = (pathExt ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(extension => extension.StartsWith('.'))
            .ToList();
        return list.Count > 0 ? list : [".COM", ".EXE", ".BAT", ".CMD"];
    }
}

/// <summary>What <see cref="CodexExecutableResolver"/> looks at, injectable so tests use a fake file system.</summary>
internal sealed record CodexResolverEnvironment(
    bool IsWindows,
    string? LocalApplicationData,
    string? ApplicationData,
    string? PathVariable,
    string? PathExtVariable,
    Architecture Architecture,
    Func<string, bool> FileExists)
{
    public static CodexResolverEnvironment Current() => new(
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("PATHEXT"),
        RuntimeInformation.OSArchitecture,
        File.Exists);
}
