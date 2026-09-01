using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using costats.App.Services;
using costats.App.Services.Updates;
using costats.Application.Pulse;
using costats.Application.RemoteView;
using costats.Application.Security;
using costats.Application.SessionActivation;
using costats.Application.Settings;
using costats.Application.Windowing;
using costats.Core.Pulse;
using costats.Core.RemoteView;
using costats.Infrastructure.Analytics;
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
    private readonly HotkeyService _hotkeyService;
    private readonly IUsageAnalyticsService _analytics;
    private readonly StartupUpdateCoordinator? _updateCoordinator;
    private AvailableUpdate? _availableUpdate;
    private readonly IAccountSourceRegistry? _accountSources;
    private readonly RemoteViewUploader? _remoteViewUploader;
    private bool _suppressHotkeyChange;
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
        HotkeyService hotkeyService,
        IUsageAnalyticsService analytics,
        IAccountSourceRegistry? accountSources = null,
        StartupUpdateCoordinator? updateCoordinator = null,
        RemoteViewUploader? remoteViewUploader = null)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _pulseOrchestrator = pulseOrchestrator;
        _accountSources = accountSources;
        _credentialVault = credentialVault;
        _copilotFetcher = copilotFetcher;
        _hotkeyService = hotkeyService;
        _analytics = analytics;
        _updateCoordinator = updateCoordinator;
        _remoteViewUploader = remoteViewUploader;

        refreshMinutes = settings.RefreshMinutes;
        startAtLogin = GetStartupRegistryValue();
        hotkeyEnabled = settings.HotkeyEnabled;
        hotkey = settings.Hotkey;
        automaticUpdateChecksEnabled = settings.AutomaticUpdateChecksEnabled;

        _settings.Accounts = settings.GetEffectiveAccounts().ToList();
        RebuildProviderRows();

        copilotEnabled = settings.CopilotEnabled;
        showOverviewResetTimes = settings.ShowOverviewResetTimes;
        showRemainingPercentages = settings.ShowRemainingPercentages;
        showWeeklyBeforeSession = settings.ShowWeeklyBeforeSession;
        showFloatingStatusPanel = settings.ShowFloatingStatusPanel;
        floatingPanelPosition = settings.FloatingPanelPosition;
        usageAlertsEnabled = settings.UsageAlertsEnabled;
        usageResetAlertsEnabled = settings.UsageResetAlertsEnabled;
        autoStartClaudeFiveHourWindow = settings.AutoStartClaudeFiveHourWindow;
        autoStartCodexFiveHourWindow = settings.AutoStartCodexFiveHourWindow;
        sessionActivationScheduleEnabled = settings.SessionActivationScheduleEnabled;
        if (!settings.HasZaiCodingKey)
        {
            // Never let a previously saved toggle become active later merely
            // because a coding-plan key was added.
            settings.AutoStartZaiFiveHourWindow = false;
        }
        autoStartZaiFiveHourWindow = settings.AutoStartZaiFiveHourWindow;

        remoteViewEnabled = settings.RemoteViewEnabled;
        remoteViewUploadUrl = settings.RemoteViewUploadUrl ?? string.Empty;
        remoteViewPageUrl = settings.RemoteViewPageUrl ?? string.Empty;
        remoteViewMessage = DescribeRemoteViewUrlProblems();

        _ = LoadCopilotTokenStatusAsync();
        RefreshUsageCacheInfo();
        RefreshUpdateAvailability();
    }

    [ObservableProperty]
    private int refreshMinutes;

    [ObservableProperty]
    private bool startAtLogin;

    [ObservableProperty]
    private bool hotkeyEnabled;

    [ObservableProperty]
    private string hotkey = "Ctrl+Alt+U";

    [ObservableProperty]
    private string hotkeyStatus = string.Empty;

    [ObservableProperty]
    private bool automaticUpdateChecksEnabled;

    [ObservableProperty]
    private string usageCacheSummary = string.Empty;

    [ObservableProperty]
    private bool isClearingUsageCache;

    [ObservableProperty]
    private bool autoStartClaudeFiveHourWindow;

    [ObservableProperty]
    private bool showRemainingPercentages;

    [ObservableProperty]
    private bool showWeeklyBeforeSession;

    [ObservableProperty]
    private bool showFloatingStatusPanel;

    [ObservableProperty]
    private string floatingPanelPosition = FloatingPanelPlacementCalculator.BottomRightSetting;

    [ObservableProperty]
    private bool usageAlertsEnabled;

    [ObservableProperty]
    private bool usageResetAlertsEnabled;

    [ObservableProperty]
    private bool autoStartCodexFiveHourWindow;

    [ObservableProperty]
    private bool autoStartZaiFiveHourWindow;

    [ObservableProperty]
    private bool sessionActivationScheduleEnabled;

    public bool CanAutoStartZaiFiveHourWindow => _settings.HasZaiCodingKey;

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

    public event EventHandler? ManualUpdateAvailable;

    public event EventHandler? TestNotificationRequested;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoteViewQrCode))]
    private ImageSource? remoteViewQrImage;

    public bool HasRemoteViewQrCode => RemoteViewQrImage is not null;

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

    public static IReadOnlyList<FloatingPanelPositionOption> FloatingPanelPositionOptions { get; } =
    [
        new(FloatingPanelPlacementCalculator.BottomRightSetting, "Bottom right"),
        new(FloatingPanelPlacementCalculator.BottomLeftSetting, "Bottom left"),
        new(FloatingPanelPlacementCalculator.TopRightSetting, "Top right"),
        new(FloatingPanelPlacementCalculator.TopLeftSetting, "Top left")
    ];

    public static IReadOnlyList<SessionActivationHourOption> SessionActivationHourOptions { get; } =
        Enumerable.Range(0, 24)
            .Select(hour => new SessionActivationHourOption(hour, $"{hour:D2}:00"))
            .ToArray();

    public SessionActivationHourOption SelectedSessionActivationScheduleStart
    {
        get => FindSessionActivationHour(_settings.SessionActivationScheduleStartHour, fallbackHour: 6);
        set
        {
            if (value is null || _settings.SessionActivationScheduleStartHour == value.Hour)
            {
                return;
            }

            _settings.SessionActivationScheduleStartHour = value.Hour;
            SaveSettingsInBackground();
            OnPropertyChanged();
            NotifySessionActivationScheduleDescriptionChanged();
        }
    }

    public SessionActivationHourOption SelectedSessionActivationScheduleEnd
    {
        get => FindSessionActivationHour(_settings.SessionActivationScheduleEndHour, fallbackHour: 18);
        set
        {
            if (value is null || _settings.SessionActivationScheduleEndHour == value.Hour)
            {
                return;
            }

            _settings.SessionActivationScheduleEndHour = value.Hour;
            SaveSettingsInBackground();
            OnPropertyChanged();
            NotifySessionActivationScheduleDescriptionChanged();
        }
    }

    public string SessionActivationScheduleDescription =>
        $"New windows may start from {SelectedSessionActivationScheduleStart.Label} until {SelectedSessionActivationScheduleEnd.Label}, using this PC's local time.";

    public bool HasInvalidSessionActivationSchedule =>
        SessionActivationScheduleEnabled &&
        (!SessionActivationSchedule.IsValidHour(_settings.SessionActivationScheduleStartHour) ||
         !SessionActivationSchedule.IsValidHour(_settings.SessionActivationScheduleEndHour) ||
         _settings.SessionActivationScheduleStartHour == _settings.SessionActivationScheduleEndHour);

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
            _pulseOrchestrator.RepublishLastState();
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private bool hasCopilotToken;

    [ObservableProperty]
    private string copilotTokenStatus = string.Empty;

    [ObservableProperty]
    private bool isCopilotTokenBusy;

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

    partial void OnHotkeyEnabledChanged(bool value)
    {
        if (_suppressHotkeyChange)
        {
            return;
        }

        var result = _hotkeyService.Apply(value, Hotkey);
        if (!result.IsSuccess)
        {
            HotkeyStatus = result.Error;
            _suppressHotkeyChange = true;
            HotkeyEnabled = _settings.HotkeyEnabled;
            _suppressHotkeyChange = false;
            return;
        }

        _settings.HotkeyEnabled = value;
        HotkeyStatus = value ? $"Active: {result.NormalizedHotkey}" : "Global shortcut is off.";
        SaveSettingsInBackground();
    }

    partial void OnHotkeyChanged(string value)
    {
        if (_suppressHotkeyChange)
        {
            return;
        }

        if (!HotkeyEnabled)
        {
            _settings.Hotkey = value.Trim();
            HotkeyStatus = "Shortcut will be validated when enabled.";
            SaveSettingsInBackground();
            return;
        }

        var result = _hotkeyService.Apply(true, value);
        if (!result.IsSuccess)
        {
            HotkeyStatus = result.Error;
            _suppressHotkeyChange = true;
            Hotkey = _settings.Hotkey;
            _suppressHotkeyChange = false;
            return;
        }

        _settings.Hotkey = result.NormalizedHotkey;
        if (!string.Equals(Hotkey, result.NormalizedHotkey, StringComparison.Ordinal))
        {
            _suppressHotkeyChange = true;
            Hotkey = result.NormalizedHotkey;
            _suppressHotkeyChange = false;
        }
        HotkeyStatus = $"Active: {result.NormalizedHotkey}";
        SaveSettingsInBackground();
    }

    partial void OnAutomaticUpdateChecksEnabledChanged(bool value)
    {
        _settings.AutomaticUpdateChecksEnabled = value;
        SaveSettingsInBackground();
    }

    private void RebuildProviderRows()
    {
        ProviderRows.Clear();
        var rows = new List<ProviderRowViewModel>();
        foreach (var account in (_settings.Accounts ?? []).Where(a => a.IsValid))
        {
            var kind = account.IsClaude ? MonitoredAccountSettings.ClaudeType : MonitoredAccountSettings.CodexType;
            var providerId = $"{kind}:{account.Id}";
            rows.Add(new ProviderRowViewModel(
                kind,
                account.Id,
                MonitoredAccountSettings.NormalizeDisplayName(account.DisplayName, account.Id),
                account.ConfigDir,
                IsPrimaryProvider(providerId),
                _settings.IsFloatingPanelProviderVisible(providerId),
                UsageAlertsEnabled: _settings.IsUsageAlertProviderEnabled(providerId),
                UsageAlertThreshold: _settings.GetUsageAlertThreshold(providerId),
                KeepSessionActive: account.KeepSessionActive));
        }

        if (_settings.HasZaiKey)
        {
            rows.Add(new ProviderRowViewModel(
                "zai",
                null,
                _settings.ZAiDisplayName,
                "API key configured",
                IsPrimaryProvider("zai"),
                _settings.IsFloatingPanelProviderVisible("zai"),
                UsageAlertsEnabled: _settings.IsUsageAlertProviderEnabled("zai"),
                UsageAlertThreshold: _settings.GetUsageAlertThreshold("zai")));
        }

        if (_settings.CopilotEnabled)
        {
            rows.Add(new ProviderRowViewModel(
                "copilot",
                null,
                "Copilot",
                "Token in Windows Credential Manager",
                IsPrimaryProvider("copilot"),
                _settings.IsFloatingPanelProviderVisible("copilot"),
                UsageAlertsEnabled: _settings.IsUsageAlertProviderEnabled("copilot"),
                UsageAlertThreshold: _settings.GetUsageAlertThreshold("copilot")));
        }

        if (_settings.EnsureFloatingPanelHasVisibleProvider(rows.Select(row => row.ProviderId)))
        {
            rows = rows.Select(row => row with { IsShownInFloatingPanel = true }).ToList();
            SaveSettingsInBackground();
        }

        var selectedCount = rows.Count(row => row.IsShownInFloatingPanel);
        foreach (var row in rows)
        {
            ProviderRows.Add(row with
            {
                CanChangeFloatingPanelSelection = !row.IsShownInFloatingPanel || selectedCount > 1
            });
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
        _pulseOrchestrator.RepublishLastState();
    }

    [RelayCommand]
    private void ToggleFloatingPanelAccount(ProviderRowViewModel? row)
    {
        if (row is null || !row.CanChangeFloatingPanelSelection)
        {
            return;
        }

        _settings.SetFloatingPanelProviderVisible(row.ProviderId, !row.IsShownInFloatingPanel);
        SaveSettingsInBackground();
        RebuildProviderRows();
        _pulseOrchestrator.RepublishLastState();
    }

    [RelayCommand]
    private void ToggleUsageAlertAccount(ProviderRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _settings.SetUsageAlertRule(
            row.ProviderId,
            !row.UsageAlertsEnabled,
            row.UsageAlertThreshold);
        SaveSettingsInBackground();
        RebuildProviderRows();
        _pulseOrchestrator.RepublishLastState();
    }

    [RelayCommand]
    private void TestNotification()
    {
        TestNotificationRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleKeepSessionActive(ProviderRowViewModel? row)
    {
        if (row is not { CanKeepSessionActive: true, AccountId: not null })
        {
            return;
        }

        var account = (_settings.Accounts ?? []).FirstOrDefault(candidate =>
            (candidate.IsClaude || candidate.IsCodex) &&
            string.Equals(candidate.Id, row.AccountId, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            return;
        }

        account.KeepSessionActive = !row.KeepSessionActive;
        ApplyAccountsChanged(account.KeepSessionActive
            ? $"Session refresh enabled for {row.Name}."
            : $"Session refresh disabled for {row.Name}.");
    }

    public void SetUsageAlertThreshold(ProviderRowViewModel row, string? text)
    {
        ArgumentNullException.ThrowIfNull(row);

        var threshold = int.TryParse(text, out var parsed)
            ? Math.Clamp(parsed, 1, 100)
            : row.UsageAlertThreshold;
        _settings.SetUsageAlertRule(row.ProviderId, row.UsageAlertsEnabled, threshold);
        SaveSettingsInBackground();
        RebuildProviderRows();
        _pulseOrchestrator.RepublishLastState();
    }

    /// <summary>Persists account changes and applies them live (no restart needed).</summary>
    private void ApplyAccountsChanged(string message = "Saved and applied.")
    {
        SaveSettingsInBackground();
        _accountSources?.Reload();
        _analytics.Invalidate();
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
            OnPropertyChanged(nameof(CanAutoStartZaiFiveHourWindow));
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

        _settings.SetFloatingPanelProviderVisible(row.ProviderId, visible: true);
        _settings.RemoveUsageAlertRule(row.ProviderId);

        switch (row.Kind)
        {
            case "zai":
                AutoStartZaiFiveHourWindow = false;
                _settings.ZAiCodingApiKey = null;
                _settings.ZAiApiKey = null;
                OnPropertyChanged(nameof(CanAutoStartZaiFiveHourWindow));
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

    partial void OnShowOverviewResetTimesChanged(bool value)
    {
        _settings.ShowOverviewResetTimes = value;
        SaveSettingsInBackground();
        _pulseOrchestrator.RepublishLastState();
    }

    partial void OnShowRemainingPercentagesChanged(bool value)
    {
        _settings.ShowRemainingPercentages = value;
        SaveSettingsInBackground();
        _pulseOrchestrator.RepublishLastState();
    }

    partial void OnShowWeeklyBeforeSessionChanged(bool value)
    {
        _settings.ShowWeeklyBeforeSession = value;
        SaveSettingsInBackground();
        _pulseOrchestrator.RepublishLastState();
    }

    partial void OnShowFloatingStatusPanelChanged(bool value)
    {
        _settings.ShowFloatingStatusPanel = value;
        SaveSettingsInBackground();
        _pulseOrchestrator.RepublishLastState();
    }

    partial void OnUsageAlertsEnabledChanged(bool value)
    {
        _settings.UsageAlertsEnabled = value;
        SaveSettingsInBackground();
        _pulseOrchestrator.RepublishLastState();
    }

    partial void OnUsageResetAlertsEnabledChanged(bool value)
    {
        _settings.UsageResetAlertsEnabled = value;
        SaveSettingsInBackground();
        _pulseOrchestrator.RepublishLastState();
    }

    partial void OnFloatingPanelPositionChanged(string value)
    {
        var normalized = FloatingPanelPlacementCalculator.NormalizeSetting(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            FloatingPanelPosition = normalized;
            return;
        }

        _settings.FloatingPanelPosition = normalized;
        SaveSettingsInBackground();
        _pulseOrchestrator.RepublishLastState();
    }

    partial void OnAutoStartClaudeFiveHourWindowChanged(bool value)
    {
        _settings.AutoStartClaudeFiveHourWindow = value;
        SaveSettingsInBackground();
    }

    partial void OnAutoStartCodexFiveHourWindowChanged(bool value)
    {
        _settings.AutoStartCodexFiveHourWindow = value;
        SaveSettingsInBackground();
    }

    partial void OnAutoStartZaiFiveHourWindowChanged(bool value)
    {
        _settings.AutoStartZaiFiveHourWindow = value;
        SaveSettingsInBackground();
    }

    partial void OnSessionActivationScheduleEnabledChanged(bool value)
    {
        _settings.SessionActivationScheduleEnabled = value;
        SaveSettingsInBackground();
        NotifySessionActivationScheduleDescriptionChanged();
    }

    private SessionActivationHourOption FindSessionActivationHour(int hour, int fallbackHour) =>
        SessionActivationHourOptions.FirstOrDefault(option => option.Hour == hour) ??
        SessionActivationHourOptions[fallbackHour];

    private void NotifySessionActivationScheduleDescriptionChanged()
    {
        OnPropertyChanged(nameof(SessionActivationScheduleDescription));
        OnPropertyChanged(nameof(HasInvalidSessionActivationSchedule));
    }

    partial void OnRemoteViewEnabledChanged(bool value)
    {
        RemoteViewQrImage = null;
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
        _pulseOrchestrator.RepublishLastState();
    }

    partial void OnRemoteViewUploadUrlChanged(string value)
    {
        _settings.RemoteViewUploadUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        RemoteViewMessage = DescribeRemoteViewUrlProblems();
        SaveSettingsInBackground();
    }

    partial void OnRemoteViewPageUrlChanged(string value)
    {
        RemoteViewQrImage = null;
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
        RemoteViewQrImage = null;
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
            _pulseOrchestrator.RepublishLastState();
        }
    }

    public void RefreshUsageCacheInfo()
    {
        UsageCacheSummary = FormatCacheInfo(_analytics.GetCacheInfo());
    }

    [RelayCommand]
    private async Task ClearUsageCacheAsync()
    {
        if (IsClearingUsageCache)
        {
            return;
        }

        IsClearingUsageCache = true;
        UsageCacheSummary = "Clearing local usage cache...";
        try
        {
            var info = await Task.Run(() => _analytics.ClearCacheAsync(CancellationToken.None)).ConfigureAwait(true);
            UsageCacheSummary = info.FileCount == 0
                ? "Usage cache cleared. The next usage report will perform a full scan."
                : FormatCacheInfo(info);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not clear usage cache");
            UsageCacheSummary = "Could not clear the usage cache.";
        }
        finally
        {
            IsClearingUsageCache = false;
        }
    }

    private static string FormatCacheInfo(UsageCacheInfo info)
    {
        var size = info.Bytes switch
        {
            >= 1024L * 1024L => $"{info.Bytes / (1024d * 1024d):0.##} MB",
            >= 1024L => $"{info.Bytes / 1024d:0.##} KB",
            _ => $"{info.Bytes} bytes"
        };
        return $"{info.FileCount:N0} cached files, {size}.";
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

    [RelayCommand]
    private void GenerateRemoteViewQrCode()
    {
        var qr = RemoteViewQrCode.Create(_settings);
        if (qr is null)
        {
            RemoteViewQrImage = null;
            RemoteViewMessage = "Enable remote view and finish the viewer URL before generating a QR code.";
            return;
        }

        using var stream = new MemoryStream(qr.PngBytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        RemoteViewQrImage = image;
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
        var promptForAvailableUpdate = false;

        try
        {
            var result = await _updateCoordinator.CheckForUpdateAsync(ct, forceCheck: true);
            ApplyUpdateCheckResult(result);
            promptForAvailableUpdate = result.Status == UpdateCheckStatus.UpdateAvailable &&
                result.Update is not null;
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

        if (promptForAvailableUpdate)
        {
            ManualUpdateAvailable?.Invoke(this, EventArgs.Empty);
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

public sealed record FloatingPanelPositionOption(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record SessionActivationHourOption(int Hour, string Label)
{
    public override string ToString() => Label;
}
