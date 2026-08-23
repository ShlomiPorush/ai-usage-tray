using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using costats.App.Services;
using costats.App.Services.Updates;
using costats.Application.Pulse;
using costats.Application.Security;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Core.RemoteView;
using costats.Infrastructure.Providers;
using Microsoft.Win32;
using Serilog;
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
    private AvailableUpdate? _availableUpdate;
    private readonly IMulticcDiscovery? _multiccDiscovery;
    private readonly IAccountSourceRegistry? _accountSources;
    private readonly RemoteViewUploader? _remoteViewUploader;
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
        IMulticcDiscovery? multiccDiscovery = null,
        RemoteViewUploader? remoteViewUploader = null)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _pulseOrchestrator = pulseOrchestrator;
        _accountSources = accountSources;
        _credentialVault = credentialVault;
        _copilotFetcher = copilotFetcher;
        _updateCoordinator = updateCoordinator;
        _multiccDiscovery = multiccDiscovery;
        _remoteViewUploader = remoteViewUploader;

        refreshMinutes = settings.RefreshMinutes;
        startAtLogin = GetStartupRegistryValue();
        showClockPanel = settings.ShowClockPanel;

        _settings.Accounts = settings.GetEffectiveAccounts().ToList();
        RebuildProviderRows();

        multiccDetected = _multiccDiscovery?.IsDetected ?? false;
        multiccEnabled = settings.MulticcEnabled;
        multiccSelectedProfile = settings.MulticcSelectedProfile;
        multiccProfileNames = _multiccDiscovery?.Profiles.Select(p => p.Name).ToList() ?? [];
        multiccProfileCount = multiccProfileNames.Count;

        copilotEnabled = settings.CopilotEnabled;
        showOverviewResetTimes = settings.ShowOverviewResetTimes;
        showPercentageLeft = settings.ShowPercentageLeft;

        remoteViewEnabled = settings.RemoteViewEnabled;
        remoteViewUploadUrl = settings.RemoteViewUploadUrl ?? string.Empty;
        remoteViewPageUrl = settings.RemoteViewPageUrl ?? string.Empty;
        remoteViewMessage = DescribeRemoteViewUrlProblems();

        _ = LoadCopilotTokenStatusAsync();
        RefreshUpdateAvailability();
    }

    [ObservableProperty]
    private int refreshMinutes;

    [ObservableProperty]
    private bool startAtLogin;

    [ObservableProperty]
    private bool showClockPanel;

    [ObservableProperty]
    private bool showPercentageLeft;

    /// <summary>One row per monitored provider: Claude/Codex accounts plus Z.AI and Copilot when configured.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ProviderRowViewModel> ProviderRows { get; } = new();

    [ObservableProperty]
    private string accountsRestartMessage = string.Empty;

    [ObservableProperty]
    private bool isCheckingForUpdates;

    [ObservableProperty]
    private bool isInstallingUpdate;

    [ObservableProperty]
    private string updateStatusText = string.Empty;

    [ObservableProperty]
    private bool hasAvailableUpdate;

    [ObservableProperty]
    private string availableUpdateVersion = string.Empty;

    [ObservableProperty]
    private string availableUpdateNotes = string.Empty;

    [ObservableProperty]
    private bool isUpdateProgressVisible;

    [ObservableProperty]
    private bool isUpdateProgressIndeterminate;

    [ObservableProperty]
    private double updateProgressPercent;

    public bool IsUpdateBusy => IsCheckingForUpdates || IsInstallingUpdate;

    partial void OnIsCheckingForUpdatesChanged(bool value) => OnPropertyChanged(nameof(IsUpdateBusy));

    partial void OnIsInstallingUpdateChanged(bool value) => OnPropertyChanged(nameof(IsUpdateBusy));

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

    [ObservableProperty]
    private bool remoteViewEnabled;

    [ObservableProperty]
    private string remoteViewUploadUrl = string.Empty;

    [ObservableProperty]
    private string remoteViewPageUrl = string.Empty;

    /// <summary>
    /// One short line under the Remote view section: a rejected endpoint URL, or
    /// the result of the last "New link" / turn-off action. Empty means nothing
    /// to say.
    /// </summary>
    [ObservableProperty]
    private string remoteViewMessage = string.Empty;

    /// <summary>
    /// The link to open on a phone: the viewer page plus the read id derived
    /// from the write id. Empty until both exist.
    /// </summary>
    public string ShareLink => _settings.RemoteViewShareLink ?? string.Empty;

    /// <summary>
    /// The endpoint boxes only appear on builds that ship without a remote-view
    /// service; otherwise remote view is a single checkbox and power users
    /// override the URLs by hand in settings.json.
    /// </summary>
    public bool ShowRemoteViewUrlFields => !_settings.HasRemoteViewDefaults;

    /// <summary>Explains what leaves the machine, worded for the shipped relay or for a self-hosted endpoint.</summary>
    public string RemoteViewHint =>
        _settings.HasRemoteViewDefaults
            ? "After each refresh, uploads a small snapshot to the built-in relay: provider, account nickname, plan, usage percentages and reset times. No tokens, credentials or folder paths are sent. The share link is read-only, and the snapshot expires server-side after about a week without updates."
            : "After each refresh, uploads a small snapshot to your endpoint: provider, account nickname, plan, usage percentages and reset times. No tokens, credentials or folder paths are sent. The snapshot expires server-side after about a week without updates.";

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
            SaveSettingsInBackground();
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
        SaveSettingsInBackground();
        OnPropertyChanged(nameof(SelectedRefreshOption));
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        _settings.StartAtLogin = value;
        SetStartupRegistryValue(value);
        SaveSettingsInBackground();
    }

    partial void OnShowClockPanelChanged(bool value)
    {
        _settings.ShowClockPanel = value;
        SaveSettingsInBackground();
        _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
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
        SaveSettingsInBackground();
        RebuildProviderRows();
        AccountsRestartMessage = row.IsPrimary ? "Primary account cleared." : $"{row.Name} set as primary.";
        _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
    }

    /// <summary>Persists account changes and applies them live (no restart needed).</summary>
    private void ApplyAccountsChanged(string message = "Saved and applied.")
    {
        SaveSettingsInBackground();
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
        SaveSettingsInBackground();
    }

    partial void OnMulticcSelectedProfileChanged(string? value)
    {
        _settings.MulticcSelectedProfile = value;
        MulticcRestartMessage = "Restart required to apply changes.";
        OnPropertyChanged(nameof(IsMulticcAllProfiles));
        SaveSettingsInBackground();
    }

    partial void OnShowOverviewResetTimesChanged(bool value)
    {
        _settings.ShowOverviewResetTimes = value;
        SaveSettingsInBackground();
        // Push a refresh so the widget picks the flag up immediately.
        _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
    }

    partial void OnShowPercentageLeftChanged(bool value)
    {
        _settings.ShowPercentageLeft = value;
        SaveSettingsInBackground();
        _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
    }

    partial void OnRemoteViewEnabledChanged(bool value)
    {
        _settings.RemoteViewEnabled = value;

        RemoteViewMessage = DescribeRemoteViewUrlProblems();

        if (value)
        {
            // The write id is minted on first enable and then kept, so the link
            // a user has already sent to their phone keeps working. Ids from
            // older versions are already 32 lowercase hex characters.
            if (!RemoteViewIds.IsValidId(_settings.RemoteViewId))
            {
                _settings.RemoteViewId = RemoteViewIds.MintWriteId();
            }
        }
        else
        {
            // Turning it off stops uploads; the stored snapshot is removed too,
            // instead of sitting on the server until the weekly expiry. The
            // write id is kept so re-enabling restores the same link.
            _ = ReportRemoteSnapshotDeleteAsync(_settings.RemoteViewId);
        }

        SaveSettingsInBackground();
        OnPropertyChanged(nameof(ShareLink));
        // Push a refresh so the widget shows or hides its remote view button immediately.
        _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
    }

    partial void OnRemoteViewUploadUrlChanged(string value)
    {
        _settings.RemoteViewUploadUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        RemoteViewMessage = DescribeRemoteViewUrlProblems();
        SaveSettingsInBackground();
    }

    partial void OnRemoteViewPageUrlChanged(string value)
    {
        _settings.RemoteViewPageUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        RemoteViewMessage = DescribeRemoteViewUrlProblems();
        SaveSettingsInBackground();
        OnPropertyChanged(nameof(ShareLink));
    }

    /// <summary>
    /// Names any endpoint override that was rejected. An override that is not
    /// https (or http on loopback) is ignored rather than used, because the
    /// snapshot and the write id travel over it.
    /// </summary>
    private string DescribeRemoteViewUrlProblems()
    {
        var badUpload = !string.IsNullOrWhiteSpace(_settings.RemoteViewUploadUrl) &&
                        !RemoteViewEndpoints.IsAllowed(_settings.RemoteViewUploadUrl);
        var badPage = !string.IsNullOrWhiteSpace(_settings.RemoteViewPageUrl) &&
                      !RemoteViewEndpoints.IsAllowed(_settings.RemoteViewPageUrl);

        return (badUpload, badPage) switch
        {
            (true, true) => "Both URLs must start with https. They are ignored until you fix them.",
            (true, false) => "Upload endpoint must start with https. It is ignored until you fix it.",
            (false, true) => "Viewer page must start with https. It is ignored until you fix it.",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Mints a fresh write id, so the previous share link can no longer be used
    /// to read this machine's usage. Deliberately confirm-free: the hint next to
    /// the button says what it costs.
    /// </summary>
    [RelayCommand]
    private void NewRemoteViewLink()
    {
        var retiredWriteId = _settings.RemoteViewId;
        _settings.RemoteViewId = RemoteViewIds.MintWriteId();
        SaveSettingsInBackground();
        OnPropertyChanged(nameof(ShareLink));
        RemoteViewMessage = "New link ready. The old one no longer updates.";

        // Best effort: the old snapshot would expire on its own within a week.
        _ = _remoteViewUploader?.DeleteAsync(retiredWriteId);

        if (_settings.RemoteViewEnabled)
        {
            // Skip the upload throttle so the link a user copies right now works.
            _remoteViewUploader?.RequestImmediateUpload();
            _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
        }
    }

    /// <summary>Deletes the remote snapshot and reports the outcome truthfully.</summary>
    private async Task ReportRemoteSnapshotDeleteAsync(string? writeId)
    {
        if (_remoteViewUploader is null || !RemoteViewIds.IsValidId(writeId))
        {
            return;
        }

        var deleted = await _remoteViewUploader.DeleteAsync(writeId).ConfigureAwait(true);

        // The user may have toggled it back on while the request was in flight.
        if (_settings.RemoteViewEnabled)
        {
            return;
        }

        RemoteViewMessage = deleted
            ? "Uploads stopped and the shared snapshot was removed."
            : "Uploads stopped. The shared snapshot expires within a week.";
    }

    [RelayCommand]
    private void CopyShareLink()
    {
        var link = ShareLink;
        if (string.IsNullOrEmpty(link))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(link);
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another process; nothing to recover.
            Debug.WriteLine($"Share link copy failed: {ex.Message}");
        }
    }

    partial void OnCopilotEnabledChanged(bool value)
    {
        _settings.CopilotEnabled = value;
        SaveSettingsInBackground();
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
            await _credentialVault.DeleteAsync(CredentialKeys.CopilotToken, CancellationToken.None);
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
        IsUpdateProgressVisible = true;
        IsUpdateProgressIndeterminate = true;
        UpdateProgressPercent = 0;
        UpdateStatusText = "Checking for updates...";

        try
        {
            var result = await _updateCoordinator.CheckForUpdateAsync(ct, forceCheck: true);
            ApplyUpdateCheckResult(result);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Update check timed out. Try again.";
        }
        catch
        {
            UpdateStatusText = "Could not check for updates.";
        }
        finally
        {
            IsCheckingForUpdates = false;
            IsUpdateProgressVisible = false;
            IsUpdateProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_updateCoordinator is null || _availableUpdate is null || IsUpdateBusy)
        {
            return;
        }

        _updateCheckCts?.Cancel();
        _updateCheckCts?.Dispose();
        _updateCheckCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = _updateCheckCts.Token;

        IsInstallingUpdate = true;
        IsUpdateProgressVisible = true;
        IsUpdateProgressIndeterminate = false;
        UpdateProgressPercent = 0;
        UpdateStatusText = "Starting download...";

        try
        {
            var progress = new Progress<UpdateProgress>(ApplyUpdateProgress);
            if (!await _updateCoordinator.DownloadAndStageUpdateAsync(_availableUpdate, progress, ct))
            {
                UpdateStatusText = "Could not prepare the update. Try again.";
                IsUpdateProgressVisible = false;
                return;
            }

            UpdateProgressPercent = 100;
            IsUpdateProgressIndeterminate = true;
            UpdateStatusText = "Installing update. AI Usage Tray will restart automatically...";

            // Let WPF render the final status before the updater asks this process to exit.
            await Task.Delay(750, ct);
            if (await _updateCoordinator.TryApplyPendingUpdateAsync(ct, manualTrigger: true))
            {
                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                    System.Windows.Application.Current.Shutdown(0));
                return;
            }

            UpdateStatusText = "Update is ready. Restart the app to install it.";
            IsUpdateProgressVisible = false;
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Update was interrupted. Try again.";
            IsUpdateProgressVisible = false;
        }
        catch
        {
            UpdateStatusText = "Could not install the update. Try again.";
            IsUpdateProgressVisible = false;
        }
        finally
        {
            IsInstallingUpdate = false;
            IsUpdateProgressIndeterminate = false;
        }
    }

    public void ApplyBackgroundUpdateResult(UpdateCheckResult result)
    {
        if (!IsUpdateBusy)
        {
            ApplyUpdateCheckResult(result, background: true);
        }
    }

    public void RefreshUpdateAvailability()
    {
        if (_updateCoordinator?.LastCheckResult is { } result)
        {
            ApplyUpdateCheckResult(result, background: true);
        }
    }

    private void ApplyUpdateCheckResult(UpdateCheckResult result, bool background = false)
    {
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable when result.Update is not null:
                _availableUpdate = result.Update;
                AvailableUpdateVersion = result.Update.Version;
                AvailableUpdateNotes = result.Update.ReleaseNotes;
                HasAvailableUpdate = true;
                UpdateStatusText = $"Version {result.Update.Version} is available.";
                break;

            case UpdateCheckStatus.UpToDate:
                ClearAvailableUpdate();
                UpdateStatusText = "You're up to date.";
                break;

            case UpdateCheckStatus.Skipped:
                if (!background)
                {
                    UpdateStatusText = "You're up to date.";
                }
                break;

            case UpdateCheckStatus.Disabled:
                ClearAvailableUpdate();
                UpdateStatusText = "Updates are not available.";
                break;

            case UpdateCheckStatus.AlreadyRunning:
                if (!background)
                {
                    UpdateStatusText = "Update check already in progress.";
                }
                break;

            case UpdateCheckStatus.CheckFailed:
                if (!background)
                {
                    UpdateStatusText = "Could not check for updates.";
                }
                break;
        }
    }

    private void ApplyUpdateProgress(UpdateProgress progress)
    {
        switch (progress.Stage)
        {
            case UpdateProgressStage.Downloading:
                IsUpdateProgressIndeterminate = !progress.Percentage.HasValue;
                if (progress.Percentage is { } percentage)
                {
                    UpdateProgressPercent = percentage;
                    UpdateStatusText = $"Downloading update... {percentage}%";
                }
                else
                {
                    UpdateStatusText = "Downloading update...";
                }
                break;

            case UpdateProgressStage.Verifying:
                IsUpdateProgressIndeterminate = true;
                UpdateStatusText = "Verifying download...";
                break;

            case UpdateProgressStage.Preparing:
                IsUpdateProgressIndeterminate = true;
                UpdateStatusText = "Preparing update...";
                break;

            case UpdateProgressStage.ReadyToInstall:
                IsUpdateProgressIndeterminate = true;
                UpdateProgressPercent = 100;
                UpdateStatusText = "Download complete. Preparing to restart...";
                break;
        }
    }

    private void ClearAvailableUpdate()
    {
        _availableUpdate = null;
        AvailableUpdateVersion = string.Empty;
        AvailableUpdateNotes = string.Empty;
        HasAvailableUpdate = false;
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


    /// <summary>
    /// Persists settings without blocking the UI thread. Failures are logged and
    /// shown to the user instead of surfacing much later as an unobserved task
    /// exception, and the caller never sees a faulted task it forgot to await.
    /// </summary>
    private void SaveSettingsInBackground()
    {
        Task saving = SaveSettingsAsync();
        // SaveSettingsAsync handles its own failures, so this task can never
        // fault and there is no unobserved-exception path left behind.
        _ = saving;
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving settings failed");
            AccountsRestartMessage = "Could not save settings. Check that %LOCALAPPDATA%\\costats is writable.";
        }
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
                shortcut.WindowStyle = 7; // WS_MINIMIZE: start minimized to tray
                shortcut.Description = "AI Usage Tray: monitors Claude, Codex, Z.AI and Copilot usage";
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
