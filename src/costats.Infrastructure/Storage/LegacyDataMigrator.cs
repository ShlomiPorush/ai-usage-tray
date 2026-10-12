using costats.Application.Storage;

namespace costats.Infrastructure.Storage;

/// <summary>
/// Moves user data from the folder used by 2.18 and earlier (<c>%LOCALAPPDATA%\costats</c>)
/// into <c>%LOCALAPPDATA%\AIUsageTray</c>, next to the installed <c>app</c>
/// folder. Runs once per start, before the log file or any store is opened.
///
/// The rules keep it safe to run on every start:
/// <list type="bullet">
/// <item>Nothing at the destination is ever overwritten. When a file already
/// exists there, the destination copy wins and the old file stays where it
/// is.</item>
/// <item>A folder that does not exist at the destination moves whole; one that
/// does is merged entry by entry, so a <c>logs</c> folder created by a new log
/// file does not strand the older log files.</item>
/// <item>A file that cannot be moved (another process holds it) is left in
/// place and tried again on the next start.</item>
/// <item>The old folder is deleted only once it is empty.</item>
/// <item>Entries named like the installer's <c>app</c> folder or the updater's
/// <c>*.__backup</c> copies are never moved into the data folder.</item>
/// </list>
/// Moves stay on one volume (both folders sit in LocalApplicationData), so
/// each move is a rename and never a partial copy.
/// </summary>
public sealed class LegacyDataMigrator
{
    private readonly string _legacyRoot;
    private readonly string _dataRoot;

    public LegacyDataMigrator(string legacyRoot, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _legacyRoot = Path.GetFullPath(legacyRoot);
        _dataRoot = Path.GetFullPath(dataRoot);
    }

    /// <summary>The real folders under LocalApplicationData.</summary>
    public static LegacyDataMigrator ForCurrentUser() =>
        new(AppDataPaths.LegacyRoot, AppDataPaths.Root);

    public LegacyDataMigrationResult Migrate()
    {
        var result = new LegacyDataMigrationResult();
        if (string.Equals(
                _legacyRoot.TrimEnd(Path.DirectorySeparatorChar),
                _dataRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        try
        {
            if (!Directory.Exists(_legacyRoot))
            {
                return result;
            }

            result.LegacyFolderFound = true;
            Directory.CreateDirectory(_dataRoot);

            foreach (var entry in EnumerateEntries(_legacyRoot, result))
            {
                var name = Path.GetFileName(entry);
                if (IsReservedRootName(name))
                {
                    result.Kept.Add(name);
                    continue;
                }

                MoveEntry(entry, Path.Combine(_dataRoot, name), name, result);
            }

            result.LegacyFolderRemoved = TryDeleteIfEmpty(_legacyRoot, result, string.Empty);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            result.Failed.Add(new LegacyDataMigrationFailure(string.Empty, ex.Message));
        }

        return result;
    }

    private static bool IsReservedRootName(string name) =>
        string.Equals(name, AppDataPaths.InstalledAppFolderName, StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".__backup", StringComparison.OrdinalIgnoreCase);

    private void MoveEntry(string source, string destination, string relative, LegacyDataMigrationResult result)
    {
        try
        {
            if (Directory.Exists(source))
            {
                MoveDirectory(source, destination, relative, result);
                return;
            }

            if (File.Exists(destination) || Directory.Exists(destination))
            {
                result.Kept.Add(relative);
                return;
            }

            File.Move(source, destination, overwrite: false);
            result.Moved.Add(relative);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            result.Failed.Add(new LegacyDataMigrationFailure(relative, ex.Message));
        }
    }

    private void MoveDirectory(string source, string destination, string relative, LegacyDataMigrationResult result)
    {
        if (File.Exists(destination))
        {
            result.Kept.Add(relative);
            return;
        }

        if (!Directory.Exists(destination))
        {
            try
            {
                Directory.Move(source, destination);
                result.Moved.Add(relative);
                return;
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                // Usually a file inside is held open. Fall back to moving the
                // entries one by one so everything that can move does.
                if (Directory.Exists(destination) || !Directory.Exists(source))
                {
                    result.Failed.Add(new LegacyDataMigrationFailure(relative, ex.Message));
                    return;
                }

                Directory.CreateDirectory(destination);
            }
        }

        foreach (var child in EnumerateEntries(source, result, relative))
        {
            var name = Path.GetFileName(child);
            MoveEntry(child, Path.Combine(destination, name), Path.Combine(relative, name), result);
        }

        TryDeleteIfEmpty(source, result, relative);
    }

    private static IReadOnlyList<string> EnumerateEntries(
        string directory,
        LegacyDataMigrationResult result,
        string relative = "")
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            result.Failed.Add(new LegacyDataMigrationFailure(relative, ex.Message));
            return [];
        }
    }

    private static bool TryDeleteIfEmpty(string directory, LegacyDataMigrationResult result, string relative)
    {
        try
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                return false;
            }

            Directory.Delete(directory, recursive: false);
            return true;
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            result.Failed.Add(new LegacyDataMigrationFailure(relative, ex.Message));
            return false;
        }
    }

    private static bool IsFileSystemError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}

/// <summary>What one migration run did. Paths are relative to the old folder.</summary>
public sealed class LegacyDataMigrationResult
{
    /// <summary>True when the old folder existed at the start of the run.</summary>
    public bool LegacyFolderFound { get; set; }

    /// <summary>True when the old folder was empty afterwards and was deleted.</summary>
    public bool LegacyFolderRemoved { get; set; }

    /// <summary>Entries moved into the data folder (a folder that moved whole counts once).</summary>
    public List<string> Moved { get; } = [];

    /// <summary>Entries left in the old folder because the destination already had them.</summary>
    public List<string> Kept { get; } = [];

    /// <summary>Entries that could not be moved this time and are retried on the next start.</summary>
    public List<LegacyDataMigrationFailure> Failed { get; } = [];

    /// <summary>True when the old folder still exists after the run.</summary>
    public bool LegacyFolderRemains => LegacyFolderFound && !LegacyFolderRemoved;
}

public sealed record LegacyDataMigrationFailure(string RelativePath, string Error);
