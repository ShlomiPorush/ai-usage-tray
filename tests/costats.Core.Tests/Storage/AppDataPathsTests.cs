using System.Text.RegularExpressions;
using costats.Application.Storage;
using Xunit;

namespace costats.Core.Tests.Storage;

public sealed class AppDataPathsTests
{
    [Fact]
    public void Data_folder_sits_next_to_the_installed_app_folder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "AIUsageTray"), AppDataPaths.ResolveRoot(null));
        Assert.Equal(Path.Combine(local, "costats"), AppDataPaths.LegacyRoot);
        Assert.Equal(Path.Combine(@"T:\base", "AIUsageTray"), AppDataPaths.ResolveRoot(@"T:\base"));
        Assert.Equal("app", AppDataPaths.InstalledAppFolderName);
    }

    [Fact]
    public void Debug_logs_live_in_their_own_folder_under_logs()
    {
        Assert.Equal(Path.Combine(AppDataPaths.Root, "logs"), AppDataPaths.LogsDirectory);
        Assert.Equal(Path.Combine(AppDataPaths.Root, "logs", "debug"), AppDataPaths.DebugLogsDirectory);
    }

    [Theory]
    [InlineData(@"C:\Users\sam\AppData\Local\AIUsageTray\app", false)]
    [InlineData(@"C:\Users\sam\AppData\Local\AIUsageTray\app\", false)]
    [InlineData(@"D:\Apps\AIUsageTray", false)]
    [InlineData(@"C:\Users\sam\AppData\Local\AIUsageTray", true)]
    [InlineData(@"c:\users\sam\appdata\local\aiusagetray\", true)]
    public void An_app_installed_straight_into_the_data_folder_keeps_the_old_folder(string appDirectory, bool usesLegacy)
    {
        const string data = @"C:\Users\sam\AppData\Local\AIUsageTray";
        const string legacy = @"C:\Users\sam\AppData\Local\costats";

        Assert.Equal(usesLegacy ? legacy : data, AppDataPaths.SelectRoot(data, legacy, appDirectory));
    }

    /// <summary>
    /// Scripts and config files cannot reference <see cref="AppDataPaths"/>,
    /// so this keeps them from drifting back to the old data folder. The
    /// uninstaller is the only file that still names it, on purpose: it
    /// removes the old folder when asked to.
    /// </summary>
    [Fact]
    public void No_file_outside_the_migration_still_writes_to_the_old_data_folder()
    {
        var repo = RepositoryRoot();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("src", "costats.Application", "Storage", "AppDataPaths.cs"),
            Path.Combine("src", "costats.Infrastructure", "Storage", "LegacyDataMigrator.cs"),
            Path.Combine("scripts", "uninstall.ps1"),
        };

        var files = new[] { "src", "scripts" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(repo, folder), "*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildOutput(path))
            .Where(path => !allowed.Contains(Path.GetRelativePath(repo, path)));

        // A data path looks like costats\x, costats/x, "costats", "x" in a
        // Path.Combine, or Join-Path ... "costats". Namespaces (costats.App)
        // and the single-instance name are not paths.
        var dataPath = new Regex(
            @"costats(\\\\|\\|/)(?!\.)|Path\.Combine\([^)]*""costats""|LocalApplicationData\)\s*,\s*""costats""|Join-Path[^\r\n]*""costats(\\\\|\\|/)?""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var offenders = files
            .Where(path => !path.EndsWith("pricing-snapshot.json", StringComparison.OrdinalIgnoreCase))
            .Where(path => dataPath.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(repo, path))
            .ToList();

        Assert.Empty(offenders);
    }

    private static bool IsBuildOutput(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        return path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) ||
               path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "costats.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
