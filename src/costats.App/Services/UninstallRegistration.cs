using System.IO;
using System.Reflection;
using Microsoft.Win32;
using Serilog;

namespace costats.App.Services
{
    /// <summary>
    /// Registers the app under Add/remove programs.
    /// </summary>
    /// <remarks>
    /// The installer is a plain ZIP extract, so nothing else ever writes an
    /// uninstall entry and the app is invisible in appwiz.cpl. The entry is
    /// rewritten on every start, which keeps DisplayVersion correct across
    /// self-updates. HKCU only: this is a per-user install and must never ask
    /// for elevation.
    /// </remarks>
    public static class UninstallRegistration
    {
        private const string KeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AIUsageTray";

        private const string DisplayName = "AI Usage Tray";
        private const string Publisher = "ShlomiPorush";
        private const string ProjectUrl = "https://github.com/ShlomiPorush/ai-usage-tray";
        private const string UninstallScriptName = "uninstall.ps1";

        /// <summary>
        /// Writes (or refreshes) the uninstall entry. Never throws: a missing
        /// Add/remove programs row must not stop the app from starting.
        /// </summary>
        public static void Refresh()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return;
                }

                var installDir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrWhiteSpace(installDir))
                {
                    return;
                }

                var script = Path.Combine(installDir, UninstallScriptName);
                if (!File.Exists(script))
                {
                    // Older ZIPs did not ship the script. Registering an entry
                    // whose Uninstall button does nothing is worse than none.
                    Log.Information(
                        "Skipping Add/remove programs registration: {Script} is not present",
                        script);
                    return;
                }

                var powershell = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                var uninstallString =
                    $"\"{powershell}\" -NoProfile -ExecutionPolicy Bypass -File \"{script}\"";

                using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
                if (key is null)
                {
                    return;
                }

                key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
                key.SetValue("DisplayVersion", CurrentVersion(), RegistryValueKind.String);
                key.SetValue("Publisher", Publisher, RegistryValueKind.String);
                key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                key.SetValue("DisplayIcon", exePath, RegistryValueKind.String);
                key.SetValue("UninstallString", uninstallString, RegistryValueKind.String);
                key.SetValue(
                    "QuietUninstallString",
                    uninstallString + " -Silent",
                    RegistryValueKind.String);
                key.SetValue("URLInfoAbout", ProjectUrl, RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", EstimatedSizeKb(installDir), RegistryValueKind.DWord);

                Log.Information("Add/remove programs entry refreshed for {Version}", CurrentVersion());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not write the Add/remove programs entry");
            }
        }

        /// <summary>The shipped version, without any build metadata suffix.</summary>
        private static string CurrentVersion()
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational[..plus] : informational;
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        /// <summary>Folder size in KB, the unit appwiz.cpl expects.</summary>
        private static int EstimatedSizeKb(string installDir)
        {
            try
            {
                long bytes = 0;
                foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        bytes += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // A file that vanished mid-scan just does not count.
                    }
                }

                return (int)Math.Clamp(bytes / 1024, 1, int.MaxValue);
            }
            catch
            {
                return 1;
            }
        }
    }
}
