using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using costats.App.Services.Updates;
using costats.Application.Pulse;
using costats.Application.Security;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Providers;
using Microsoft.Win32;
using System.Linq;

namespace costats.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IPulseOrchestrator _pulseOrchestrator;
    private readonly ICredentialVault _credentialVault;
    private readonly CopilotUsageFetcher _copilotFetcher;
    private readonly StartupUpdateCoordinator? _updateCoordinator;
    private readonly IMulticcDiscovery? _multiccDiscovery;
    private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    // Renamed from "costats" so the entry does not collide with upstream builds.
    private const string AppName = "AiUsageTray";
    private const string StartupShortcutName = "AI Usage Tray.lnk";

    public SettingsViewModel(
        ISettingsStore settingsStore,
        AppSettings settings,
        IPulseOrchestrator pulseOrchestrator,
        ICredentialVault credentialVault,
        CopilotUsageFetcher copilotFetcher,
        StartupUpdateCoordinator? updateCoordinator = null,
        IMulticcDiscovery? multiccDiscovery = null)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _pulseOrchestrator = pulseOrchestrator;
        _credentialVault = credentialVault;
        _copilotFetcher = copilotFetcher;
        _updateCoordinator = updateCoordinator;
        _multiccDiscovery = multiccDiscovery;

        refreshMinutes = settings.RefreshMinutes;
        startAtLogin = GetStartupRegistryValue();

        EnsureOpenAiAccounts();
        openAiAccount1Name = OpenAiAccountSettings.NormalizeDisplayName(
            settings.OpenAiAccounts[0].DisplayName,
            "OpenAI 1");
        openAiAccount2Name = OpenAiAccountSettings.NormalizeDisplayName(
            settings.OpenAiAccounts[1].DisplayName,
            "OpenAI 2");

        multiccDetected = _multiccDiscovery?.IsDetected ?? false;
        multiccEnabled = settings.MulticcEnabled;
        multiccSelectedProfile = settings.MulticcSelectedProfile;
        multiccProfileNames = _multiccDiscovery?.Profiles.Select(p => p.Name).ToList() ?? [];
        multiccProfileCount = multiccProfileNames.Count;

        copilotEnabled = settings.CopilotEnabled;
        _ = LoadCopilotTokenStatusAsync();
    }

    [ObservableProperty]
    private int refreshMinutes;

    [ObservableProperty]
    private bool startAtLogin;

    [ObservableProperty]
    private string openAiAccount1Name = "OpenAI 1";

    [ObservableProperty]
    private string openAiAccount2Name = "OpenAI 2";

    [ObservableProperty]
    private string openAiNamesRestartMessage = string.Empty;

    [ObservableProperty]
    private bool isCheckingForUpdates;

    [ObservableProperty]
    private string updateStatusText = string.Empty;

    [ObservableProperty]
    private bool multiccDetected;

    [ObservableProperty]
    private bool multiccEnabled;

    [ObservableProperty]
    private string? multiccSelectedProfile;

    [ObservableProperty]
    private IReadOnlyList<string> multiccProfileNames = [];

    [ObservableProperty]
    private int multiccProfileCount;

    [ObservableProperty]
    private string multiccRestartMessage = string.Empty;

    [ObservableProperty]
    private bool copilotEnabled;

    [ObservableProperty]
    private bool hasCopilotToken;

    [ObservableProperty]
    private string copilotTokenStatus = string.Empty;

    [ObservableProperty]
    private bool isCopilotTokenBusy;

    public string ClaudeConfigDir => _settings.ClaudeConfigDir;

    public bool IsMulticcAllProfiles => MulticcSelectedProfile is null;

    public string Version { get; } =
        (Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown")
        .Split('+')[0];

    public static IReadOnlyList<RefreshOption> RefreshOptions { get; } = new[]
    {
        new RefreshOption(1, "1 minute"),
        new RefreshOption(2, "2 minutes"),
        new RefreshOption(3, "3 minutes"),
        new RefreshOption(5, "5 minutes"),
        new RefreshOption(10, "10 minutes"),
        new RefreshOption(15, "15 minutes"),
    };

    public RefreshOption SelectedRefreshOption
    {
        get => RefreshOptions.FirstOrDefault(o => o.Minutes == RefreshMinutes) ?? RefreshOptions[3];
        set
        {
            if (value is not null && RefreshMinutes != value.Minutes)
            {
                RefreshMinutes = value.Minutes;
                OnPropertyChanged();
            }
        }
    }

    partial void OnRefreshMinutesChanged(int value)
    {
        _settings.RefreshMinutes = value;
        _pulseOrchestrator.UpdateRefreshInterval(TimeSpan.FromMinutes(value));
        _ = SaveSettingsAsync();
        OnPropertyChanged(nameof(SelectedRefreshOption));
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        _settings.StartAtLogin = value;
        SetStartupRegistryValue(value);
        _ = SaveSettingsAsync();
    }

    partial void OnOpenAiAccount1NameChanged(string value)
    {
        SaveOpenAiAccountName(0, value, "OpenAI 1", name => openAiAccount1Name = name, nameof(OpenAiAccount1Name));
    }

    partial void OnOpenAiAccount2NameChanged(string value)
    {
        SaveOpenAiAccountName(1, value, "OpenAI 2", name => openAiAccount2Name = name, nameof(OpenAiAccount2Name));
    }

    private void SaveOpenAiAccountName(
        int index,
        string value,
        string fallback,
        Action<string> updateBackingField,
        string propertyName)
    {
        EnsureOpenAiAccounts();
        var normalized = OpenAiAccountSettings.NormalizeDisplayName(value, fallback);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            updateBackingField(normalized);
            OnPropertyChanged(propertyName);
        }

        _settings.OpenAiAccounts[index].DisplayName = normalized;
        OpenAiNamesRestartMessage = "Names saved. Restart AI Usage Tray to apply them.";
        _ = SaveSettingsAsync();
    }

    partial void OnMulticcEnabledChanged(bool value)
    {
        _settings.MulticcEnabled = value;
        MulticcRestartMessage = "Restart required to apply changes.";
        _ = SaveSettingsAsync();
    }

    partial void OnMulticcSelectedProfileChanged(string? value)
    {
        _settings.MulticcSelectedProfile = value;
        MulticcRestartMessage = "Restart required to apply changes.";
        OnPropertyChanged(nameof(IsMulticcAllProfiles));
        _ = SaveSettingsAsync();
    }

    partial void OnCopilotEnabledChanged(bool value)
    {
        _settings.CopilotEnabled = value;
        _ = SaveSettingsAsync();
        _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
    }

    public async Task SaveCopilotTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            CopilotTokenStatus = "Copilot token is required.";
            return;
        }

        IsCopilotTokenBusy = true;
        try
        {
            var trimmedToken = token.Trim();
            await _credentialVault.SaveAsync(CredentialKeys.CopilotToken, trimmedToken, CancellationToken.None);
            var validation = await _copilotFetcher.FetchAsync(trimmedToken, CancellationToken.None);
            HasCopilotToken = true;
            CopilotTokenStatus = validation.Status == CopilotFetchStatus.Success
                ? "Copilot token saved."
                : validation.StatusSummary;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copilot token save failed: {ex.Message}");
            CopilotTokenStatus = "Could not save Copilot token.";
        }
        finally
        {
            IsCopilotTokenBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearCopilotTokenAsync()
    {
        IsCopilotTokenBusy = true;
        try
        {
            await _credentialVault.SaveAsync(CredentialKeys.CopilotToken, string.Empty, CancellationToken.None);
            HasCopilotToken = false;
            CopilotTokenStatus = "Copilot token cleared.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copilot token clear failed: {ex.Message}");
            CopilotTokenStatus = "Could not clear Copilot token.";
        }
        finally
        {
            IsCopilotTokenBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_updateCoordinator is null)
        {
            UpdateStatusText = "Updates are not available.";
            return;
        }

        // Cancel any previous in-flight check before starting a new one
        _updateCheckCts?.Cancel();
        _updateCheckCts?.Dispose();
        _updateCheckCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = _updateCheckCts.Token;

        IsCheckingForUpdates = true;
        UpdateStatusText = "Checking for updates...";

        try
        {
            var result = await Task.Run(() => _updateCoordinator.CheckAndStageUpdateAsync(ct, forceCheck: true), ct);

            switch (result)
            {
                case UpdateCheckResult.UpdateStaged:
                case UpdateCheckResult.UpdateAlreadyStaged:
                    UpdateStatusText = "Update found. Restarting...";
                    if (await Task.Run(() => _updateCoordinator.TryApplyPendingUpdateAsync(ct, manualTrigger: true), ct))
                    {
                        // Use BeginInvoke to avoid any potential deadlock with synchronous Invoke
                        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                            System.Windows.Application.Current.Shutdown(0));
                    }
                    else
                    {
                        UpdateStatusText = "Update staged. Restart to apply.";
                        IsCheckingForUpdates = false;
                    }
                    break;

                case UpdateCheckResult.UpToDate:
                case UpdateCheckResult.Skipped:
                    UpdateStatusText = "You're up to date.";
                    IsCheckingForUpdates = false;
                    break;

                case UpdateCheckResult.Disabled:
                    UpdateStatusText = "Updates are not available.";
                    IsCheckingForUpdates = false;
                    break;

                case UpdateCheckResult.AlreadyRunning:
                    UpdateStatusText = "Update check already in progress.";
                    IsCheckingForUpdates = false;
                    break;

                case UpdateCheckResult.CheckFailed:
                default:
                    UpdateStatusText = "Could not check for updates.";
                    IsCheckingForUpdates = false;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Update check timed out. Try again.";
            IsCheckingForUpdates = false;
        }
        catch
        {
            UpdateStatusText = "Could not check for updates.";
            IsCheckingForUpdates = false;
        }
    }

    private CancellationTokenSource? _updateCheckCts;

    private async Task LoadCopilotTokenStatusAsync()
    {
        try
        {
            var token = await _credentialVault.LoadAsync(CredentialKeys.CopilotToken, CancellationToken.None);
            HasCopilotToken = !string.IsNullOrWhiteSpace(token);
            CopilotTokenStatus = HasCopilotToken ? string.Empty : "Copilot token not set.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copilot token load failed: {ex.Message}");
            CopilotTokenStatus = "Could not load Copilot token.";
        }
    }

    private void EnsureOpenAiAccounts()
    {
        _settings.OpenAiAccounts ??= [];
        while (_settings.OpenAiAccounts.Count < 2)
        {
            var index = _settings.OpenAiAccounts.Count + 1;
            _settings.OpenAiAccounts.Add(new OpenAiAccountSettings
            {
                Id = $"openai-{index}",
                DisplayName = $"OpenAI {index}",
                CodexHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    $".codex-openai-{index}")
            });
        }
    }

    private async Task SaveSettingsAsync()
    {
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
    }

    private static bool GetStartupRegistryValue()
    {
        try
        {
            // The "Start at login" state is whichever of these is set. Either
            // is sufficient for Windows to launch the app on login; we write
            // both on enable.
            if (HasRegistryValue())
            {
                return true;
            }
            return GetStartupShortcutPath() is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasRegistryValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false);
        return key?.GetValue(AppName) is not null;
    }

    private static string? GetStartupShortcutPath()
    {
        try
        {
            var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var candidate = Path.Combine(startupDir, StartupShortcutName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SetStartupRegistryValue(bool enable)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

        // 1. Registry: HKCU\...\Run\AiUsageTray = "path"
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true);
            if (key is not null)
            {
                if (enable)
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
        }
        catch
        {
            // Registry writes can fail under locked-down policies; the
            // startup-folder shortcut below is our backup.
        }

        // 2. Startup-folder shortcut: belt-and-suspenders. Some Windows
        // configurations or cleanup tools strip Run-key entries but leave
        // the Startup folder alone. We write both so the app survives
        // either path.
        try
        {
            WriteStartupShortcut(enable, exePath);
        }
        catch
        {
            // Best effort. If both writes fail, the user can still launch
            // AIUsageTray manually or via Task Scheduler.
        }
    }

    private static void WriteStartupShortcut(bool enable, string exePath)
    {
        var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var shortcutPath = Path.Combine(startupDir, StartupShortcutName);

        if (!enable)
        {
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }
            return;
        }

        // Create the Startup folder if it doesn't exist (rare on a fresh
        // user profile, but defensively correct).
        Directory.CreateDirectory(startupDir);

        // Use WScript.Shell via late-bound COM to create the .lnk without
        // taking a hard COM reference in the .csproj.
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            var shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
                shortcut.WindowStyle = 7; // WS_MINIMIZE — start minimized to tray
                shortcut.Description = "AI Usage Tray — monitors Codex, Claude, and MiniMax usage";
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}

public sealed record RefreshOption(int Minutes, string Label)
{
    public override string ToString() => Label;
}
