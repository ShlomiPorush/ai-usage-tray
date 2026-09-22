using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Analytics;
using Serilog;

namespace costats.App.ViewModels;

/// <summary>
/// Drives the first-run setup window. Provider authentication remains owned by
/// the official CLIs; this view model only opens them and checks their normal
/// usage sources afterwards.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject, IObserver<PulseState>, IDisposable
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore _settingsStore;
    private readonly IAccountSourceRegistry _accountSources;
    private readonly IPulseOrchestrator _orchestrator;
    private readonly IUsageAnalyticsService _analytics;
    private readonly PulseViewModel _pulseViewModel;
    private readonly IDisposable _pulseSubscription;
    private CancellationTokenSource? _checkCts;

    public OnboardingViewModel(
        AppSettings settings,
        ISettingsStore settingsStore,
        IAccountSourceRegistry accountSources,
        IPulseOrchestrator orchestrator,
        IUsageAnalyticsService analytics,
        PulseViewModel pulseViewModel)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _accountSources = accountSources;
        _orchestrator = orchestrator;
        _analytics = analytics;
        _pulseViewModel = pulseViewModel;
        _pulseSubscription = orchestrator.PulseStream.Subscribe(this);
    }

    [ObservableProperty]
    private int step = 1;

    [ObservableProperty]
    private bool monitorClaude = true;

    [ObservableProperty]
    private bool monitorCodex = true;

    [ObservableProperty]
    private bool isChecking;

    [ObservableProperty]
    private bool claudeReady;

    [ObservableProperty]
    private bool codexReady;

    [ObservableProperty]
    private string claudeStatusLabel = "Not checked";

    [ObservableProperty]
    private string codexStatusLabel = "Not checked";

    [ObservableProperty]
    private string claudeStatusDetail = "We will check the standard Claude Code profile.";

    [ObservableProperty]
    private string codexStatusDetail = "We will check the standard Codex profile.";

    public bool IsChooseStep => Step == 1;
    public bool IsConnectStep => Step == 2;
    public bool IsDoneStep => Step == 3;
    public bool CanGoBack => Step > 1;
    public bool HasSelectedProviders => MonitorClaude || MonitorCodex;
    public bool CanContinue => Step switch
    {
        1 => HasSelectedProviders,
        2 => HasSelectedProviders &&
             (!MonitorClaude || ClaudeReady) &&
             (!MonitorCodex || CodexReady) &&
             !IsChecking,
        3 => true,
        _ => false
    };

    public string PrimaryActionText => Step switch
    {
        2 => "Continue",
        3 => "Open AI usage",
        _ => "Continue"
    };

    public string ConnectedSummary
    {
        get
        {
            var count = (MonitorClaude && ClaudeReady ? 1 : 0) + (MonitorCodex && CodexReady ? 1 : 0);
            return count == 1
                ? "1 account will refresh automatically in the background."
                : $"{count} accounts will refresh automatically in the background.";
        }
    }

    partial void OnStepChanged(int value) => NotifyNavigationState();
    partial void OnMonitorClaudeChanged(bool value) => NotifyNavigationState();
    partial void OnMonitorCodexChanged(bool value) => NotifyNavigationState();
    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(CanContinue));

    partial void OnClaudeReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ConnectedSummary));
    }

    partial void OnCodexReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ConnectedSummary));
    }

    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(IsChooseStep));
        OnPropertyChanged(nameof(IsConnectStep));
        OnPropertyChanged(nameof(IsDoneStep));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(HasSelectedProviders));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(ConnectedSummary));
    }

    /// <summary>Resets the window for a first display or a widget-initiated resume.</summary>
    public async Task PrepareAsync(bool resume, bool previewOnly = false, int previewStep = 1)
    {
        CancelCheck();
        var accounts = _settings.GetEffectiveAccounts();
        MonitorClaude = accounts.Any(account => account.IsClaude);
        MonitorCodex = accounts.Any(account => account.IsCodex);
        SetNotCheckedStatuses();

        Step = resume && HasSelectedProviders ? 2 : 1;
        if (previewOnly && previewStep is >= 2 and <= 3)
        {
            Step = previewStep;
            ClaudeReady = true;
            ClaudeStatusLabel = "Ready";
            ClaudeStatusDetail = "Claude Code profile found and connected.";
            CodexReady = previewStep == 3;
            CodexStatusLabel = CodexReady ? "Ready" : "Sign-in required";
            CodexStatusDetail = CodexReady
                ? "Codex profile found and connected."
                : "Codex is installed, but this profile is not signed in.";
            return;
        }

        if (Step == 2 && !previewOnly)
        {
            await CheckConnectionsAsync().ConfigureAwait(true);
        }
    }

    public async Task ContinueAsync()
    {
        if (!CanContinue)
        {
            return;
        }

        if (Step == 1)
        {
            await SaveSelectionAsync(OnboardingStates.Started).ConfigureAwait(true);
            Step = 2;
            await CheckConnectionsAsync().ConfigureAwait(true);
            return;
        }

        if (Step == 2)
        {
            Step = 3;
        }
    }

    public void Back()
    {
        if (Step > 1)
        {
            Step--;
        }
    }

    public async Task DismissAsync()
    {
        CancelCheck();
        await SaveSelectionAsync(OnboardingStates.AfterDismissal(_settings.OnboardingState)).ConfigureAwait(true);
        RefreshInBackground();
    }

    public async Task CompleteAsync()
    {
        CancelCheck();
        await SaveSelectionAsync(OnboardingStates.Completed).ConfigureAwait(true);
        RefreshInBackground();
    }

    [RelayCommand]
    private async Task RetryAsync() => await CheckConnectionsAsync().ConfigureAwait(true);

    [RelayCommand]
    private void OpenClaudeSignIn()
    {
        var path = GetSelectedAccount(MonitoredAccountSettings.ClaudeType)?.ConfigDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var literal = QuotePowerShellLiteral(path);
        var command = $"$env:CLAUDE_CONFIG_DIR={literal}; Write-Host 'Claude Code will open. Use /login if needed, then return to AI Usage Tray and click Retry.' -ForegroundColor Cyan; claude";
        if (TryOpenPowerShell(command))
        {
            ClaudeStatusDetail = "Complete sign-in in PowerShell, then return here and click Retry.";
        }
        else
        {
            ClaudeStatusDetail = "Could not open PowerShell. Sign in with Claude Code, then click Retry.";
        }
    }

    [RelayCommand]
    private void OpenCodexSignIn()
    {
        var path = GetSelectedAccount(MonitoredAccountSettings.CodexType)?.ConfigDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var literal = QuotePowerShellLiteral(path);
        var command = $"$env:CODEX_HOME={literal}; Write-Host 'Complete Codex sign-in, then return to AI Usage Tray and click Retry.' -ForegroundColor Cyan; codex login";
        if (TryOpenPowerShell(command))
        {
            CodexStatusDetail = "Complete sign-in in PowerShell, then return here and click Retry.";
        }
        else
        {
            CodexStatusDetail = "Could not open PowerShell. Run codex login, then click Retry.";
        }
    }

    private async Task CheckConnectionsAsync()
    {
        CancelCheck();
        var cts = new CancellationTokenSource();
        _checkCts = cts;
        IsChecking = true;

        if (MonitorClaude)
        {
            ClaudeReady = false;
            ClaudeStatusLabel = "Checking";
            ClaudeStatusDetail = "Checking the Claude Code profile...";
        }
        if (MonitorCodex)
        {
            CodexReady = false;
            CodexStatusLabel = "Checking";
            CodexStatusDetail = "Checking the official Codex app-server...";
        }

        try
        {
            _accountSources.Reload();
            await _orchestrator.RefreshOnceAsync(RefreshTrigger.Silent, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Onboarding account check failed");
        }
        finally
        {
            if (ReferenceEquals(_checkCts, cts))
            {
                _checkCts = null;
                IsChecking = false;
                SetUnresolvedStatusAfterCheck();
            }
            cts.Dispose();
        }
    }

    private void ApplyPulse(PulseState state)
    {
        if (state.IsRefreshing)
        {
            return;
        }

        ApplyProviderReading(
            MonitoredAccountSettings.ClaudeType,
            MonitorClaude,
            state,
            (ready, label, detail) =>
            {
                ClaudeReady = ready;
                ClaudeStatusLabel = label;
                ClaudeStatusDetail = detail;
            });

        ApplyProviderReading(
            MonitoredAccountSettings.CodexType,
            MonitorCodex,
            state,
            (ready, label, detail) =>
            {
                CodexReady = ready;
                CodexStatusLabel = label;
                CodexStatusDetail = detail;
            });
    }

    private void ApplyProviderReading(
        string type,
        bool selected,
        PulseState state,
        Action<bool, string, string> apply)
    {
        if (!selected)
        {
            apply(false, "Not selected", "This provider will not be monitored.");
            return;
        }

        var account = GetSelectedAccount(type);
        if (account is null)
        {
            apply(false, "Needs attention", "The account profile is not configured.");
            return;
        }

        var providerId = $"{type}:{account.Id}";
        if (!state.Providers.TryGetValue(providerId, out var reading))
        {
            return;
        }

        if (reading.Usage is not null)
        {
            apply(true, "Ready", type == MonitoredAccountSettings.ClaudeType
                ? "Claude Code profile found and connected."
                : "Codex profile found and connected.");
            return;
        }

        var detail = string.IsNullOrWhiteSpace(reading.StatusSummary)
            ? "Sign in with the official CLI, then retry."
            : reading.StatusSummary;
        apply(false, "Sign-in required", detail);
    }

    private void SetNotCheckedStatuses()
    {
        ClaudeReady = false;
        CodexReady = false;
        ClaudeStatusLabel = MonitorClaude ? "Not checked" : "Not selected";
        CodexStatusLabel = MonitorCodex ? "Not checked" : "Not selected";
        ClaudeStatusDetail = MonitorClaude
            ? "We will check the standard Claude Code profile."
            : "This provider will not be monitored.";
        CodexStatusDetail = MonitorCodex
            ? "We will check the standard Codex profile."
            : "This provider will not be monitored.";
    }

    private void SetUnresolvedStatusAfterCheck()
    {
        if (MonitorClaude && ClaudeStatusLabel == "Checking")
        {
            ClaudeStatusLabel = "Sign-in required";
            ClaudeStatusDetail = "Claude did not return subscription usage. Sign in, then retry.";
        }
        if (MonitorCodex && CodexStatusLabel == "Checking")
        {
            CodexStatusLabel = "Sign-in required";
            CodexStatusDetail = "Codex is not installed, not signed in, or did not return rate limits.";
        }
    }

    private async Task SaveSelectionAsync(string state)
    {
        var current = _settings.GetEffectiveAccounts();
        var selected = new List<MonitoredAccountSettings>();

        if (MonitorClaude)
        {
            selected.AddRange(current.Where(account => account.IsClaude));
            if (!selected.Any(account => account.IsClaude))
            {
                selected.Add(CreateDefaultAccount(MonitoredAccountSettings.ClaudeType));
            }
        }

        if (MonitorCodex)
        {
            selected.AddRange(current.Where(account => account.IsCodex));
            if (!selected.Any(account => account.IsCodex))
            {
                selected.Add(CreateDefaultAccount(MonitoredAccountSettings.CodexType));
            }
        }

        _settings.Accounts = selected;
        _settings.OnboardingState = state;
        _accountSources.Reload();
        _analytics.Invalidate();
        await _settingsStore.SaveAsync(_settings, CancellationToken.None).ConfigureAwait(true);
        _pulseViewModel.NotifyOnboardingStateChanged();
    }

    private MonitoredAccountSettings? GetSelectedAccount(string type) =>
        _settings.GetEffectiveAccounts().FirstOrDefault(account =>
            string.Equals(account.Type, type, StringComparison.OrdinalIgnoreCase));

    private static MonitoredAccountSettings CreateDefaultAccount(string type)
    {
        var isClaude = type == MonitoredAccountSettings.ClaudeType;
        var name = isClaude ? "Claude" : "Codex";
        return new MonitoredAccountSettings
        {
            Id = isClaude ? "claude-1" : "codex-1",
            Type = type,
            DisplayName = name,
            ConfigDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                isClaude ? ".claude" : ".codex")
        };
    }

    private static string QuotePowerShellLiteral(string value) => $"'{value.Replace("'", "''")}'";

    private static bool TryOpenPowerShell(string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            startInfo.ArgumentList.Add("-NoExit");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open provider sign-in in PowerShell");
            return false;
        }
    }

    private void RefreshInBackground()
    {
        _ = _orchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None)
            .ContinueWith(
                task => Log.Warning(task.Exception!.GetBaseException(), "Onboarding refresh failed"),
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    private void CancelCheck()
    {
        var cts = _checkCts;
        _checkCts = null;
        if (cts is null)
        {
            return;
        }

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void OnNext(PulseState value)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ApplyPulse(value));
    }

    public void OnError(Exception error) => Log.Warning(error, "Onboarding pulse stream failed");
    public void OnCompleted() { }

    public void Dispose()
    {
        CancelCheck();
        _pulseSubscription.Dispose();
    }
}
