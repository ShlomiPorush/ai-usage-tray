namespace costats.Infrastructure.Antigravity;

/// <summary>
/// Finds the Antigravity CLI. Only file existence is checked; nothing in the
/// agy folders is read. The first match wins: the official installer location
/// <c>%LOCALAPPDATA%\agy\bin\agy.exe</c>, then <c>agy.exe</c> in each rooted
/// PATH folder in order. Returns null when agy is not installed.
/// </summary>
public static class AgyExecutableResolver
{
    public const string ExecutableName = "agy.exe";

    public static string? Resolve() => Resolve(AgyResolverEnvironment.Current());

    public static string? Resolve(AgyResolverEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.IsWindows)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(environment.LocalApplicationData))
        {
            var installed = Path.Combine(environment.LocalApplicationData, "agy", "bin", ExecutableName);
            if (SafeExists(environment, installed))
            {
                return installed;
            }
        }

        foreach (var directory in (environment.PathVariable ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(entry => entry.Trim('"'))
                     .Where(entry => entry.Length > 0 && Path.IsPathFullyQualified(entry)))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, ExecutableName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (SafeExists(environment, candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool SafeExists(AgyResolverEnvironment environment, string path)
    {
        try
        {
            return environment.FileExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>What <see cref="AgyExecutableResolver"/> looks at, injectable so tests use a fake file system.</summary>
public sealed record AgyResolverEnvironment(
    bool IsWindows,
    string? LocalApplicationData,
    string? PathVariable,
    Func<string, bool> FileExists)
{
    public static AgyResolverEnvironment Current() => new(
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("PATH"),
        File.Exists);
}
