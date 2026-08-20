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
    private readonly IAccountSourceRegistry? _accountSources;
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
        IAccountSourceRegistry? accountSources = null,
        StartupUpdateCoordinator? updateCoordinator = null,
        IMulticcDiscovery? multiccDiscovery = null)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _pulseOrchestrator = pulseOrchestrator;
        _accountSources = accountSources;
        _credentialVault = credentialVault;
        _copilotFetcher = copilotFetcher;
        _updateCoordinator = updateCoordinator;
        _multiccDiscovery = multiccDiscovery;

        refreshMinutes = settings.RefreshMinutes;
        startAtLogin = GetStartupRegistryValue();

        _settings.Accounts = settings.GetEffectiveAccounts().ToList();
        RebuildProviderRows();

        multiccDetected = _multiccDiscovery?.IsDetected ?? false;
        multiccEnabled = settings.MulticcEnabled;
        multiccSelectedProfile = settings.MulticcSelectedProfile;
        multiccProfileNames = _multiccDiscovery?.Profiles.Select(p => p.Name).ToList() ?? [];
        multiccProfileCount = multiccProfileNames.Count;

        copilotEnabled = settings.CopilotEnabled;
        showOverviewResetTimes = settings.ShowOverviewResetTimes;
        _ = LoadCopilotTokenStatusAsync();
    }

    [ObservableProperty]
    private int refreshMinutes;

    [ObservableProperty]
    private bool startAtLogin;

    /// <summary>One row per monitored provider: Claude/Codex accounts plus Z.AI and Copilot when configured.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ProviderRowViewModel> ProviderRows { get; } = new();

    [ObservableProperty]
    private string accountsRestartMessage = string.Empty;

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
    private bool showOverviewResetTimes;

    public static IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new ThemeOption(Services.ThemeService.SystemTheme, "Follow system"),
        new ThemeOption(Services.ThemeService.LightTheme, "Light"),
        new ThemeOption(Services.ThemeService.DarkTheme, "Dark"),
    ];

    public ThemeOption SelectedTheme
    {
        get => ThemeOptions.FirstOrDefault(o =>
                   string.Equals(o.Value, _settings.Theme, StringComparison.OrdinalIgnoreCase))
               ?? ThemeOptions[0];
        set
        {
            if (value is null || string.Equals(_settings.Theme, value.Value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.Theme = value.Value;
            _ = SaveSettingsAsync();
            Services.ThemeService.Apply(value.Value);
            // Refresh so view-model-computed colours (percent text) match the new theme.
            _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private bool hasCopilotToken;

    [ObservableProperty]
    private string copilotTokenStatus = string.Empty;

    [ObservableProperty]
    private bool isCopilotTokenBusy;

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

    private void RebuildProviderRows()
    {
        ProviderRows.Clear();
        foreach (var account in (_settings.Accounts ?? []).Where(a => a.IsValid))
        {
            var kind = account.IsClaude ? MonitoredAccountSettings.ClaudeType : MonitoredAccountSettings.CodexType;
            ProviderRows.Add(new ProviderRowViewModel(
                kind,
                account.Id,
                MonitoredAccountSettings.NormalizeDisplayName(account.DisplayName, account.Id),
                account.ConfigDir,
                IsPrimaryProvider($"{kind}:{account.Id}")));
        }

        if (_settings.HasZaiKey)
        {
            ProviderRows.Add(new ProviderRowViewModel("zai", null, _settings.ZAiDisplayName, "API key configured", IsPrimaryProvider("zai")));
        }

        if (_settings.CopilotEnabled)
        {
            ProviderRows.Add(new ProviderRowViewModel("copilot", null, "Copilot", "Token in Windows Credential Manager", IsPrimaryProvider("copilot")));
        }
    }

    private bool IsPrimaryProvider(string providerId) =>
        string.Equals(_settings.PrimaryAccountId, providerId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Toggles the primary account: the tray icon shows this account's status
    /// and it is pinned to the top of the widget overview.
    /// </summary>
    [RelayCommand]
    private void SetPrimaryRow(ProviderRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _settings.PrimaryAccountId = row.IsPrimary ? null : row.ProviderId;
        _ = SaveSettingsAsync();
        RebuildProviderRows();
        AccountsRestartMessage = row.IsPrimary ? "Primary account cleared." : $"{row.Name} set as primary.";
        _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
    }

    /// <summary>Persists account changes and applies them live (no restart needed).</summary>
    private void ApplyAccountsChanged(string message = "Saved and applied.")
    {
        _ = SaveSettingsAsync();
        _accountSources?.Reload();
        RebuildProviderRows();
        AccountsRestartMessage = message;
        _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
    }

    /// <summary>Adds a Claude/Codex account from the Add-account dialog.</summary>
    public void AddAccountFromDialog(string type, string displayName, string configDir)
    {
        _settings.Accounts ??= [];
        _settings.Accounts.Add(new MonitoredAccountSettings
        {
            Id = NextAccountId(type),
            Type = type,
            DisplayName = MonitoredAccountSettings.NormalizeDisplayName(displayName, type),
            ConfigDir = configDir
        });
        ApplyAccountsChanged();
    }

    /// <summary>Updates an existing Claude/Codex account from the Edit dialog.</summary>
    public void UpdateAccountFromDialog(string accountId, string displayName, string configDir)
    {
        var account = (_settings.Accounts ?? []).FirstOrDefault(a =>
            string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            return;
        }

        account.DisplayName = MonitoredAccountSettings.NormalizeDisplayName(displayName, account.Id);
        account.ConfigDir = configDir;
        ApplyAccountsChanged();
    }

    /// <summary>Stores the Z.AI key + display name. An empty key keeps the existing one (edit mode).</summary>
    public void ConfigureZai(string displayName, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _settings.ZAiCodingApiKey = apiKey;
        }
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            _settings.ZAiDisplayName = displayName.Trim();
        }

        ApplyAccountsChanged("Z.AI configured.");
    }

    /// <summary>Enables Copilot and stores its token. An empty token keeps the existing one (edit mode).</summary>
    public void ConfigureCopilot(string token)
    {
        _settings.CopilotEnabled = true;
        CopilotEnabled = true;
        if (!string.IsNullOrWhiteSpace(token))
        {
            _ = SaveCopilotTokenAsync(token);
        }

        ApplyAccountsChanged("Copilot configured.");
    }

    private string NextAccountId(string type)
    {
        for (var index = 1; ; index++)
        {
            var candidate = $"{type}-{index}";
            if (!(_settings.Accounts ?? []).Any(a => string.Equals(a.Id, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    [RelayCommand]
    private void RemoveProviderRow(ProviderRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        switch (row.Kind)
        {
            case "zai":
                _settings.ZAiCodingApiKey = null;
                _settings.ZAiApiKey = null;
                ApplyAccountsChanged("Z.AI removed.");
                break;

            case "copilot":
                _settings.CopilotEnabled = false;
                CopilotEnabled = false;
                _ = ClearCopilotTokenAsync();
                ApplyAccountsChanged("Copilot removed.");
                break;

            default:
                _settings.Accounts?.RemoveAll(a =>
                    string.Equals(a.Id, row.AccountId, StringComparison.OrdinalIgnoreCase));
                if (row.IsPrimary)
                {
                    _settings.PrimaryAccountId = null;
                }
                ApplyAccountsChanged("Account removed.");
                break;
        }
    }

    [RelayCommand]
    private void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return;
        }

        // Relaunch after a short delay so the single-instance mutex is released
        // before the new process tries to acquire it.
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        System.Windows.Application.Current.Shutdown(0);
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

    partial void OnShowOverviewResetTimesChanged(bool value)
    {
        _settings.ShowOverviewResetTimes = value;
        _ = SaveSettingsAsync();
        // Push a refresh so the widget picks the flag up immediately.
        _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
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

public sealed record ThemeOption(string Value, string Label)
{
    public override string ToString() => Label;
}
