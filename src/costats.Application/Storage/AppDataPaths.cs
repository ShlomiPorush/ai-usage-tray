namespace costats.Application.Storage;

/// <summary>
/// The one place that knows where the app keeps its per-user data. Everything
/// the app writes (settings, logs, caches, state files, update staging) lives
/// under <c>%LOCALAPPDATA%\AIUsageTray</c>, next to the installed <c>app</c>
/// folder that the installer and updater replace. Builds up to 2.18 used
/// <c>%LOCALAPPDATA%\costats</c>; <see cref="LegacyFolderName"/> exists only so
/// the startup migration and the uninstaller can find that old folder.
/// </summary>
public static class AppDataPaths
{
    /// <summary>Data folder name under LocalApplicationData.</summary>
    public const string FolderName = "AIUsageTray";

    /// <summary>Data folder name used by 2.18 and earlier.</summary>
    public const string LegacyFolderName = "costats";

    /// <summary>
    /// Sibling of the data files that holds the installed app. The installer
    /// and updater own it, so nothing the app stores may use this name.
    /// </summary>
    public const string InstalledAppFolderName = "app";

    public const string LogsFolderName = "logs";
    public const string DebugLogsFolderName = "debug";

    /// <summary>%LOCALAPPDATA%.</summary>
    public static string LocalApplicationData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly Lazy<string> DefaultRoot = new(() =>
        SelectRoot(ResolveRoot(null), LegacyRoot, AppContext.BaseDirectory));

    /// <summary>
    /// %LOCALAPPDATA%\AIUsageTray. The one exception is an app installed
    /// straight into that folder instead of its <c>app</c> subfolder: the
    /// installer and updater replace the install folder whole, so data kept
    /// there would be lost, and such an install keeps using the old
    /// %LOCALAPPDATA%\costats folder instead.
    /// </summary>
    public static string Root => DefaultRoot.Value;

    /// <summary>
    /// Picks the data folder for an app running from <paramref name="appDirectory"/>.
    /// Separate from <see cref="Root"/> so the rule can be tested.
    /// </summary>
    public static string SelectRoot(string dataRoot, string legacyRoot, string? appDirectory) =>
        !string.IsNullOrWhiteSpace(appDirectory) && IsSameDirectory(appDirectory, dataRoot)
            ? legacyRoot
            : dataRoot;

    private static bool IsSameDirectory(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>%LOCALAPPDATA%\costats, the data folder of 2.18 and earlier.</summary>
    public static string LegacyRoot => Path.Combine(LocalApplicationData, LegacyFolderName);

    /// <summary>%LOCALAPPDATA%\AIUsageTray\logs.</summary>
    public static string LogsDirectory => Path.Combine(Root, LogsFolderName);

    /// <summary>%LOCALAPPDATA%\AIUsageTray\logs\debug, written only while debug logging is on.</summary>
    public static string DebugLogsDirectory => Path.Combine(LogsDirectory, DebugLogsFolderName);

    /// <summary>
    /// Returns the data folder inside <paramref name="basePath"/>, or inside
    /// LocalApplicationData when it is blank. Stores take a base path so tests
    /// can point them at a temp folder and never touch real user data.
    /// </summary>
    public static string ResolveRoot(string? basePath) =>
        Path.Combine(
            string.IsNullOrWhiteSpace(basePath) ? LocalApplicationData : basePath,
            FolderName);

    /// <summary>A path inside the data folder, for example <c>Combine("pricing.json")</c>.</summary>
    public static string Combine(params string[] parts) =>
        Path.Combine([Root, .. parts]);
}
