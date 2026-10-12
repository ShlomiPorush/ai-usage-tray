using costats.Application.Storage;
using costats.Infrastructure.Storage;
using Xunit;

namespace costats.Core.Tests.Storage;

/// <summary>
/// The one-time move from %LOCALAPPDATA%\costats to %LOCALAPPDATA%\AIUsageTray.
/// Both roots are temp folders; nothing here touches real user data.
/// </summary>
public sealed class LegacyDataMigratorTests : IDisposable
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), "ai-usage-tray-migration-tests", Guid.NewGuid().ToString("N"));

    private string Legacy => Path.Combine(_base, AppDataPaths.LegacyFolderName);
    private string Data => Path.Combine(_base, AppDataPaths.FolderName);

    public LegacyDataMigratorTests()
    {
        // The installed app folder already exists in a real data root.
        Directory.CreateDirectory(Path.Combine(Data, AppDataPaths.InstalledAppFolderName));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }

    private LegacyDataMigrator Migrator() => new(Legacy, Data);

    private void WriteLegacy(string relative, string content)
    {
        var path = Path.Combine(Legacy, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteData(string relative, string content)
    {
        var path = Path.Combine(Data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ReadData(string relative) => File.ReadAllText(Path.Combine(Data, relative));

    [Fact]
    public void Fresh_install_without_an_old_folder_does_nothing()
    {
        var result = Migrator().Migrate();

        Assert.False(result.LegacyFolderFound);
        Assert.Empty(result.Moved);
        Assert.Empty(result.Failed);
        Assert.False(Directory.Exists(Legacy));
        Assert.Equal([AppDataPaths.InstalledAppFolderName], Directory.EnumerateFileSystemEntries(Data).Select(Path.GetFileName));
    }

    [Fact]
    public void Moves_every_file_and_folder_and_removes_the_empty_old_folder()
    {
        WriteLegacy("settings.json", "{\"refreshMinutes\":7}");
        WriteLegacy("pricing-catalog.json", "catalog");
        WriteLegacy("usage-reset-state.json", "[]");
        WriteLegacy(Path.Combine("logs", "costats-20261001.log"), "old log");
        WriteLegacy(Path.Combine("cache", "usage", "a.json"), "cache");
        WriteLegacy(Path.Combine("session-activation-work", "x.txt"), "work");
        Directory.CreateDirectory(Path.Combine(Legacy, "updates", "downloads"));

        var result = Migrator().Migrate();

        Assert.True(result.LegacyFolderFound);
        Assert.True(result.LegacyFolderRemoved);
        Assert.False(result.LegacyFolderRemains);
        Assert.Empty(result.Failed);
        Assert.Empty(result.Kept);
        Assert.False(Directory.Exists(Legacy));
        Assert.Equal("{\"refreshMinutes\":7}", ReadData("settings.json"));
        Assert.Equal("catalog", ReadData("pricing-catalog.json"));
        Assert.Equal("old log", ReadData(Path.Combine("logs", "costats-20261001.log")));
        Assert.Equal("cache", ReadData(Path.Combine("cache", "usage", "a.json")));
        Assert.Equal("work", ReadData(Path.Combine("session-activation-work", "x.txt")));
        Assert.True(Directory.Exists(Path.Combine(Data, "updates", "downloads")));
        Assert.True(Directory.Exists(Path.Combine(Data, AppDataPaths.InstalledAppFolderName)));
    }

    [Fact]
    public void Never_overwrites_the_destination_and_keeps_only_the_colliding_file()
    {
        WriteLegacy("settings.json", "old settings");
        WriteLegacy(Path.Combine("logs", "costats-20261012.log"), "old day");
        WriteLegacy(Path.Combine("logs", "costats-20261013.log"), "old today");
        WriteData("settings.json", "new settings");
        // The new log file for today was created before the move ran.
        WriteData(Path.Combine("logs", "costats-20261013.log"), "new today");

        var result = Migrator().Migrate();

        Assert.Equal("new settings", ReadData("settings.json"));
        Assert.Equal("new today", ReadData(Path.Combine("logs", "costats-20261013.log")));
        // The non-colliding sibling in the same folder still moved.
        Assert.Equal("old day", ReadData(Path.Combine("logs", "costats-20261012.log")));
        Assert.False(File.Exists(Path.Combine(Legacy, "logs", "costats-20261012.log")));

        // The colliding files stay in the old folder, which is therefore kept.
        Assert.Equal("old settings", File.ReadAllText(Path.Combine(Legacy, "settings.json")));
        Assert.Equal("old today", File.ReadAllText(Path.Combine(Legacy, "logs", "costats-20261013.log")));
        Assert.Contains("settings.json", result.Kept);
        Assert.Contains(Path.Combine("logs", "costats-20261013.log"), result.Kept);
        Assert.True(result.LegacyFolderRemains);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public void Running_again_is_harmless()
    {
        WriteLegacy("settings.json", "old settings");
        WriteLegacy(Path.Combine("cache", "x.json"), "cache");

        var first = Migrator().Migrate();
        var second = Migrator().Migrate();

        Assert.True(first.LegacyFolderRemoved);
        Assert.False(second.LegacyFolderFound);
        Assert.Empty(second.Moved);
        Assert.Equal("old settings", ReadData("settings.json"));
        Assert.Equal("cache", ReadData(Path.Combine("cache", "x.json")));
    }

    [Fact]
    public void Running_again_after_a_collision_changes_nothing()
    {
        WriteLegacy("settings.json", "old settings");
        WriteData("settings.json", "new settings");

        Migrator().Migrate();
        var second = Migrator().Migrate();

        Assert.True(second.LegacyFolderFound);
        Assert.Empty(second.Moved);
        Assert.Equal(["settings.json"], second.Kept);
        Assert.Equal("new settings", ReadData("settings.json"));
        Assert.Equal("old settings", File.ReadAllText(Path.Combine(Legacy, "settings.json")));
    }

    [Fact]
    public void A_locked_file_stays_behind_and_moves_on_the_next_start()
    {
        WriteLegacy("settings.json", "settings");
        WriteLegacy(Path.Combine("logs", "costats-20261012.log"), "closed log");
        WriteLegacy(Path.Combine("logs", "costats-20261013.log"), "open log");

        LegacyDataMigrationResult locked;
        // A second running instance (or an old one) still holds today's log.
        using (new FileStream(
                   Path.Combine(Legacy, "logs", "costats-20261013.log"),
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.Read))
        {
            locked = Migrator().Migrate();
        }

        Assert.Equal("settings", ReadData("settings.json"));
        Assert.Equal("closed log", ReadData(Path.Combine("logs", "costats-20261012.log")));
        Assert.Contains(locked.Failed, failure => failure.RelativePath == Path.Combine("logs", "costats-20261013.log"));
        Assert.True(locked.LegacyFolderRemains);
        Assert.True(File.Exists(Path.Combine(Legacy, "logs", "costats-20261013.log")));

        var retry = Migrator().Migrate();

        Assert.Empty(retry.Failed);
        Assert.True(retry.LegacyFolderRemoved);
        Assert.Equal("open log", ReadData(Path.Combine("logs", "costats-20261013.log")));
        Assert.False(Directory.Exists(Legacy));
    }

    [Fact]
    public void Never_moves_entries_that_belong_to_the_installer_or_updater()
    {
        WriteLegacy(Path.Combine("app", "stray.txt"), "not data");
        WriteLegacy(Path.Combine("app.__backup", "AIUsageTray.exe"), "backup");
        WriteLegacy("settings.json", "settings");

        var result = Migrator().Migrate();

        Assert.Equal("settings", ReadData("settings.json"));
        Assert.False(File.Exists(Path.Combine(Data, "app", "stray.txt")));
        Assert.False(Directory.Exists(Path.Combine(Data, "app.__backup")));
        Assert.True(File.Exists(Path.Combine(Legacy, "app", "stray.txt")));
        Assert.Contains("app", result.Kept);
        Assert.Contains("app.__backup", result.Kept);
        Assert.True(result.LegacyFolderRemains);
    }

    [Fact]
    public void Same_folder_for_old_and_new_is_a_no_op()
    {
        WriteData("settings.json", "settings");

        var result = new LegacyDataMigrator(Data, Data).Migrate();

        Assert.False(result.LegacyFolderFound);
        Assert.Equal("settings", ReadData("settings.json"));
    }
}
