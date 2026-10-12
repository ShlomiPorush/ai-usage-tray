using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using costats.Core.Shell;
using Microsoft.Win32;
using Serilog;

namespace costats.App.Services
{
    /// <summary>
    /// Shows the tray icon on the taskbar instead of the hidden overflow on
    /// Windows 11, once, the first time Explorer records it.
    /// </summary>
    /// <remarks>
    /// Explorer keeps one key per tray icon under
    /// <c>HKCU\Control Panel\NotifyIconSettings</c>, with the owning exe in
    /// <c>ExecutablePath</c>. A DWORD <c>IsPromoted</c> of 1 shows the icon
    /// immediately. The value is written only when it is absent: a 0 or 1 that
    /// already exists is the user's own choice from taskbar settings and is never
    /// changed. Windows 10 has no such key, so nothing happens there.
    /// </remarks>
    public static class TrayIconPromotionService
    {
        private const string NotifyIconSettingsKey = @"Control Panel\NotifyIconSettings";
        private const string ExecutablePathValue = "ExecutablePath";
        private const string IsPromotedValue = "IsPromoted";

        // Explorer writes our key shortly after the icon is created, sometimes a
        // few seconds later on a busy logon.
        private const int MaxAttempts = 10;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

        /// <summary>Starts the promotion off the UI thread. Never throws.</summary>
        public static void PromoteInBackground()
        {
            _ = Task.Run(PromoteAsync);
        }

        private static async Task PromoteAsync()
        {
            try
            {
                var processPath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(processPath) || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                {
                    return;
                }

                for (var attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    if (TryPromote(processPath))
                    {
                        return;
                    }

                    await Task.Delay(RetryDelay).ConfigureAwait(false);
                }

                Log.Debug("Tray icon settings key for {ProcessPath} did not appear; overflow placement unchanged", processPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Tray icon promotion failed");
            }
        }

        /// <summary>
        /// Applies the rule to every key that belongs to this exe (older installs
        /// can leave several). Returns true once at least one matching key was
        /// found, whether or not anything needed writing.
        /// </summary>
        private static bool TryPromote(string processPath)
        {
            using var root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey, writable: false);
            if (root is null)
            {
                return false;
            }

            var found = false;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var iconKey = root.OpenSubKey(name, writable: false);
                    if (iconKey?.GetValue(ExecutablePathValue) is not string storedPath ||
                        !NotifyIconPathMatcher.Matches(storedPath, processPath, KnownFolders.TryGetPath))
                    {
                        continue;
                    }

                    found = true;
                    if (iconKey.GetValue(IsPromotedValue) is not null)
                    {
                        continue;
                    }

                    using var writableKey = root.OpenSubKey(name, writable: true);
                    if (writableKey is null || writableKey.GetValue(IsPromotedValue) is not null)
                    {
                        continue;
                    }

                    writableKey.SetValue(IsPromotedValue, 1, RegistryValueKind.DWord);
                    Log.Information("Tray icon promoted to the taskbar (NotifyIconSettings\\{Key})", name);
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                    Log.Warning(ex, "Could not update tray icon settings key {Key}", name);
                }
            }

            return found;
        }

        /// <summary>Resolves Windows known-folder GUIDs such as LocalAppData.</summary>
        private static class KnownFolders
        {
            public static string? TryGetPath(Guid folderId)
            {
                var result = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var pathPointer);
                try
                {
                    return result == 0 ? Marshal.PtrToStringUni(pathPointer) : null;
                }
                finally
                {
                    if (pathPointer != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(pathPointer);
                    }
                }
            }

            [DllImport("shell32.dll", ExactSpelling = true)]
            private static extern int SHGetKnownFolderPath(
                [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
                uint dwFlags,
                IntPtr hToken,
                out IntPtr ppszPath);
        }
    }
}
