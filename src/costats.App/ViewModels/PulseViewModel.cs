using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Analytics;
using costats.Core.Pulse;
using costats.Infrastructure.Analytics;
using Serilog;

namespace costats.App.ViewModels;

public sealed partial class PulseViewModel : ObservableObject, IObserver<PulseState>, IDisposable
{
    /// <summary>Days the detail view's "Last 30 days" row covers, today included.</summary>
    private const int CostWindowDays = 30;

    private readonly IPulseOrchestrator _orchestrator;
    private readonly AppSettings _settings;
    private readonly IDisposable _subscription;
    private readonly IEnumerable<ISignalSource> _staticSources;
    private readonly IAccountSourceRegistry _accountSources;
    private readonly IUsageAnalyticsService _analytics;
    private CancellationTokenSource? _costLoad;

    public PulseViewModel(
        IPulseOrchestrator orchestrator,
        AppSettings settings,
        IEnumerable<ISignalSource> sources,
        IAccountSourceRegistry accountSources,
        IUsageAnalyticsService analytics)
    {
        _orchestrator = orchestrator;
        _settings = settings;
        isCopilotEnabled = settings.CopilotEnabled;
        remoteViewLink = settings.RemoteViewShareLink;
        _staticSources = sources;
        _accountSources = accountSources;
        _analytics = analytics ?? throw new ArgumentNullException(nameof(analytics));

        Providers = new ObservableCollection<ProviderPulseViewModel>();
        _subscription = orchestrator.PulseStream.Subscribe(this);
    }

    // Recomputed per update so account renames/additions apply without restart.
    private Dictionary<string, string> CurrentDisplayNames() => _staticSources
        .Concat(_accountSources.Current)
        .Select(source => source.Profile)
        .GroupBy(profile => profile.ProviderId, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ProviderPulseViewModel> Providers { get; }

    [ObservableProperty]
    private string lastUpdated = "Never";

    [ObservableProperty]
    private ProviderPulseViewModel claude = new();

    [ObservableProperty]
    private ProviderPulseViewModel codex = new();

    [ObservableProperty]
    private ProviderPulseViewModel copilot = new();

    [ObservableProperty]
    private string updatedLabel = "Updated never";

    [ObservableProperty]
    private int selectedTabIndex;

    /// <summary>True while the widget shows the all-accounts overview; false in single-account detail.</summary>
    [ObservableProperty]
    private bool isOverview = true;

    /// <summary>Mirrors AppSettings.ShowOverviewResetTimes for the overview cards.</summary>
    [ObservableProperty]
    private bool showResetTimes;

    /// <summary>
    /// The remote view link, or null while remote view is off or unconfigured.
    /// Mirrors AppSettings so the overview button follows the Settings toggle.
    /// </summary>
    [ObservableProperty]
    private string? remoteViewLink;

    /// <summary>True when the overview can offer a one-click remote view button.</summary>
    public bool CanOpenRemoteView => !string.IsNullOrEmpty(RemoteViewLink);

    partial void OnRemoteViewLinkChanged(string? value) => OnPropertyChanged(nameof(CanOpenRemoteView));

    /// <summary>
    /// Copies the settings the widget reads directly into observable state. Runs
    /// on every pulse and whenever the widget is reopened, which is how Settings
    /// changes reach the widget without a restart.
    /// </summary>
    private void ApplySettings()
    {
        IsCopilotEnabled = _settings.CopilotEnabled;
        ShowResetTimes = _settings.ShowOverviewResetTimes;
        RemoteViewLink = _settings.RemoteViewShareLink;
    }

    [RelayCommand]
    private void OpenRemoteView()
    {
        var link = RemoteViewLink;
        if (string.IsNullOrEmpty(link))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // No browser, or the shell refused the URL; nothing to recover.
            System.Diagnostics.Debug.WriteLine($"Opening the remote view failed: {ex.Message}");
        }
    }

    [ObservableProperty]
    private ProviderPulseViewModel selectedAccount = new();

    [RelayCommand]
    private void OpenAccount(ProviderPulseViewModel? account)
    {
        if (account is null)
        {
            return;
        }

        SelectedAccount.HideEmail();
        account.HideEmail();
        SelectedAccount = account;
        IsOverview = false;
        BeginCostLoad(account);
    }

    [RelayCommand]
    private void BackToOverview()
    {
        // Nothing is watching the answer once the overview is back.
        _costLoad?.Cancel();
        SelectedAccount.HideEmail();
        IsOverview = true;
    }

    /// <summary>
    /// Starts (or restarts) the detail view's Cost section load for one account.
    /// The scan runs on the thread pool and its result is cached for a couple of
    /// minutes, so reopening a card costs almost nothing.
    /// </summary>
    private void BeginCostLoad(ProviderPulseViewModel account)
    {
        _costLoad?.Cancel();
        var cts = new CancellationTokenSource();
        _costLoad = cts;
        _ = LoadCostAsync(account, cts);
    }

    private async Task LoadCostAsync(ProviderPulseViewModel account, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            var known = await Task.Run(() => _analytics.GetAccountsAsync(token), token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            // Z.AI and Copilot have no local token log, and a Codex account only
            // resolves once its shared sessions folder has been scanned.
            var binding = UsageAccountMap.Resolve(account.ProviderId, known);
            if (binding is null)
            {
                return;
            }

            var today = DateOnly.FromDateTime(DateTime.Now);
            var range = UsageDateRange.LastDays(CostWindowDays, today);
            string[] filter = [binding.AccountId];

            // One report answers both rows: the 30-day totals are the window and
            // today is its last daily bucket, so the engine aggregates once.
            var report = await Task.Run(() => _analytics.GetReportAsync(range, filter, token), token).ConfigureAwait(true);
            if (token.IsCancellationRequested || report.IsEmpty)
            {
                return;
            }

            var todayTotals = report.Daily.FirstOrDefault(day => day.Day == today)?.Totals ?? UsageTotals.Empty;
            account.ApplyUsageCost(binding, todayTotals, report.Totals);
        }
        catch (OperationCanceledException)
        {
            // A newer detail view replaced this one; its load is the live one.
        }
        catch (Exception exception)
        {
            // The Cost section simply stays hidden; the rest of the detail view
            // is unaffected.
            Log.Warning(exception, "Account cost load failed for {ProviderId}", account.ProviderId);
        }
        finally
        {
            if (ReferenceEquals(_costLoad, cts))
            {
                _costLoad = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Called when the widget is (re)opened so it always starts at the overview.</summary>
    public void ResetToOverview()
    {
        ApplySettings();
        SelectedAccount.HideEmail();
        IsOverview = true;
    }

    [ObservableProperty]
    private bool isRefreshing = true; // Start true to show spinner on initial load

    [ObservableProperty]
    private bool isMulticcActive;

    [ObservableProperty]
    private bool isCopilotEnabled;

    [ObservableProperty]
    private string multiccSummary = string.Empty;

    // Aggregate cost/token totals across all multicc profiles
    [ObservableProperty]
    private string multiccTotalTodayCost = "--";

    [ObservableProperty]
    private string multiccTotalTodayTokens = "--";

    [ObservableProperty]
    private string multiccTotalWeekCost = "--";

    [ObservableProperty]
    private string multiccTotalWeekTokens = "--";

    [ObservableProperty]
    private bool hasMulticcTotals;

    public ObservableCollection<ProviderPulseViewModel> ClaudeProfiles { get; } = new();
    public ObservableCollection<ProviderPulseViewModel> CodexProfiles { get; } = new();

    /// <summary>
    /// Returns the currently selected provider based on tab index.
    /// </summary>
    public ProviderPulseViewModel SelectedProvider => SelectedTabIndex switch
    {
        0 => Codex,
        1 => Claude,
        _ => IsCopilotEnabled ? Copilot : Codex
    };

    /// <summary>
    /// Returns the provider ID for the currently selected tab.
    /// </summary>
    public string SelectedProviderId
    {
        get
        {
            if (SelectedTabIndex == 0)
                return string.IsNullOrWhiteSpace(Codex.ProviderId) ? "codex:openai-1" : Codex.ProviderId;

            if (SelectedTabIndex == 1)
            {
                // For multiple Claude accounts, return the first (worst-case) profile's ID for targeted refresh
                if (IsMulticcActive && ClaudeProfiles.Count > 0)
                    return ClaudeProfiles[0].ProviderId;

                return string.IsNullOrWhiteSpace(Claude.ProviderId) ? "claude" : Claude.ProviderId;
            }

            return IsCopilotEnabled ? "copilot" : "codex";
        }
    }

    partial void OnCodexChanged(ProviderPulseViewModel value)
    {
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedProviderId));
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedProviderId));
    }

    /// <summary>
    /// Silently refresh the currently selected provider (no loading indicator).
    /// </summary>
    public async Task RefreshSelectedProviderSilentlyAsync()
    {
        try
        {
            if (IsOverview)
            {
                await _orchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
            }
            else
            {
                await _orchestrator.RefreshProviderAsync(SelectedAccount.ProviderId, CancellationToken.None);
            }
        }
        catch
        {
            // Silent refresh failures are non-blocking
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Show loading indicator immediately for responsive UX
        IsRefreshing = true;
        try
        {
            await _orchestrator.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Log but don't crash - refresh failures should not take down the app
            System.Diagnostics.Debug.WriteLine($"Refresh failed: {ex.Message}");
        }
        finally
        {
            // Ensure loading indicator is hidden even if orchestrator doesn't publish
            IsRefreshing = false;
        }
    }

    public void OnNext(PulseState value)
    {
        // Use BeginInvoke (async) instead of Invoke to avoid blocking the UI thread
        // This allows window deactivation to work even during data updates
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            ApplySettings();
            if (!IsCopilotEnabled && SelectedTabIndex > 1)
            {
                SelectedTabIndex = 0;
            }

            IsRefreshing = value.IsRefreshing;

            // Only update provider data if we have providers (keep last state during refresh)
            if (value.Providers.Count > 0)
            {
                // ── Build all data in local variables first (no UI mutations yet) ──
                var newProviders = new List<ProviderPulseViewModel>();
                var codexProfileList = new List<ProviderPulseViewModel>();
                var claudeProfileList = new List<ProviderPulseViewModel>();
                ProviderPulseViewModel? newClaude = null;
                ProviderPulseViewModel? newCopilot = null;

                // Aggregate cost/token totals across multicc profiles
                decimal totalTodayCost = 0;
                long totalTodayTokens = 0;
                decimal totalWeekCost = 0;
                long totalWeekTokens = 0;
                var displayNames = CurrentDisplayNames();
                var today = DateOnly.FromDateTime(DateTime.Now);
                var weekStart = today.AddDays(-((int)today.DayOfWeek == 0 ? 6 : (int)today.DayOfWeek - 1)); // Monday

                foreach (var (providerId, reading) in value.Providers)
                {
                    var displayName = displayNames.TryGetValue(providerId, out var name) ? name : providerId;
                    var vm = ProviderPulseViewModel.FromReading(
                        reading,
                        displayName,
                        _settings.ShowPercentageLeft);

                    if (providerId.Equals("copilot", StringComparison.OrdinalIgnoreCase) && !IsCopilotEnabled)
                    {
                        continue;
                    }

                    // Z.AI without an API key is just noise - hide it entirely.
                    if (providerId.Equals("zai", StringComparison.OrdinalIgnoreCase) && !_settings.HasZaiKey)
                    {
                        continue;
                    }

                    newProviders.Add(vm);

                    if (providerId.StartsWith("codex:", StringComparison.OrdinalIgnoreCase))
                    {
                        codexProfileList.Add(vm);
                    }
                    else if (providerId.Equals("claude", StringComparison.OrdinalIgnoreCase))
                    {
                        newClaude = vm;
                    }
                    else if (providerId.Equals("copilot", StringComparison.OrdinalIgnoreCase))
                    {
                        newCopilot = vm;
                    }
                    else if (providerId.StartsWith("claude:", StringComparison.OrdinalIgnoreCase))
                    {
                        claudeProfileList.Add(vm);

                        // Accumulate totals from raw reading data
                        if (reading.Usage?.Consumption is { } c)
                        {
                            totalTodayCost += c.TodayCostUsd;
                            totalTodayTokens += c.TodayTokens.TotalConsumed;

                            // Compute this week from daily breakdown (Mon-Sun)
                            foreach (var slice in c.DailyBreakdown)
                            {
                                if (slice.Period >= weekStart && slice.Period <= today)
                                {
                                    totalWeekCost += slice.ComputedCostUsd;
                                    totalWeekTokens += slice.Tokens.TotalConsumed;
                                }
                            }
                        }
                    }
                }

                codexProfileList.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

                // Sort claude profiles by session utilization descending (worst-first)
                claudeProfileList.Sort((a, b) => b.SessionProgress.CompareTo(a.SessionProgress));

                // Single Claude account renders in the normal single-provider view;
                // the stacked multi panel only appears for two or more accounts.
                if (claudeProfileList.Count > 0)
                {
                    newClaude = claudeProfileList[0]; // worst-case for backward compat
                }
                var isMulticc = claudeProfileList.Count > 1;

                // Build summary text
                var summaryText = string.Empty;
                if (isMulticc)
                {
                    var total = claudeProfileList.Count;
                    var critical = claudeProfileList.Count(p => p.SessionProgress >= 0.95);
                    var warning = claudeProfileList.Count(p => p.SessionProgress >= 0.80 && p.SessionProgress < 0.95);

                    if (critical > 0)
                        summaryText = $"{total} profiles  ·  {critical} at limit, {warning} warning";
                    else if (warning > 0)
                        summaryText = $"{total} profiles  ·  {warning} near limit";
                    else
                        summaryText = $"{total} profiles  ·  All healthy";
                }

                // ── Apply to observable state (batched, single render frame) ──

                // Set scalar properties before collection changes to prevent layout thrash.
                // IsMulticcActive controls panel visibility, so setting it first ensures the
                // correct panel stays visible while collections are swapped.
                var selectedCodexId = Codex.ProviderId;
                var selectedCodex = codexProfileList.FirstOrDefault(profile =>
                                        profile.ProviderId.Equals(selectedCodexId, StringComparison.OrdinalIgnoreCase))
                                    ?? codexProfileList.FirstOrDefault();
                if (selectedCodex is not null) Codex = selectedCodex;
                if (newClaude is not null) Claude = newClaude;
                if (newCopilot is not null) Copilot = newCopilot;
                IsMulticcActive = isMulticc;
                MulticcSummary = summaryText;

                // Multicc aggregate totals
                if (isMulticc && (totalTodayTokens > 0 || totalWeekTokens > 0))
                {
                    MulticcTotalTodayCost = UsageFormatter.FormatCurrency(totalTodayCost);
                    MulticcTotalTodayTokens = UsageFormatter.FormatTokenCount(totalTodayTokens);
                    MulticcTotalWeekCost = UsageFormatter.FormatCurrency(totalWeekCost);
                    MulticcTotalWeekTokens = UsageFormatter.FormatTokenCount(totalWeekTokens);
                    HasMulticcTotals = true;
                }
                else
                {
                    HasMulticcTotals = false;
                }

                // Swap collection contents (single clear + add, no double-clear)
                CodexProfiles.Clear();
                foreach (var profile in codexProfileList) CodexProfiles.Add(profile);

                // Overview order: Claude accounts, Codex accounts, then the rest.
                static int KindRank(ProviderPulseViewModel vm) => vm.ProviderKind switch
                {
                    "claude" => 0,
                    "codex" => 1,
                    "zai" => 2,
                    _ => 3
                };
                var primaryId = _settings.PrimaryAccountId;
                foreach (var candidate in newProviders)
                {
                    candidate.IsPrimary = !string.IsNullOrWhiteSpace(primaryId) &&
                        candidate.ProviderId.Equals(primaryId, StringComparison.OrdinalIgnoreCase);
                }
                newProviders.Sort((a, b) =>
                {
                    // Primary account is pinned to the top of the overview.
                    if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
                    var rank = KindRank(a).CompareTo(KindRank(b));
                    return rank != 0 ? rank : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                });

                Providers.Clear();
                foreach (var p in newProviders) Providers.Add(p);

                // Keep the open detail view bound to the refreshed instance of the same account.
                if (!IsOverview)
                {
                    var refreshedSelection = newProviders.FirstOrDefault(p =>
                        p.ProviderId.Equals(SelectedAccount.ProviderId, StringComparison.OrdinalIgnoreCase));
                    if (refreshedSelection is not null)
                    {
                        // The refreshed instance starts with an empty Cost
                        // section; carry the loaded one over so it does not
                        // blink out, then reload it behind the scenes.
                        refreshedSelection.CopyUsageCostFrom(SelectedAccount);
                        SelectedAccount = refreshedSelection;
                        BeginCostLoad(refreshedSelection);
                    }
                    else
                    {
                        IsOverview = true; // account was removed in Settings
                    }
                }

                ClaudeProfiles.Clear();
                foreach (var p in claudeProfileList) ClaudeProfiles.Add(p);
            }

            // Only notify SelectedProvider if the reference actually changed
            OnPropertyChanged(nameof(SelectedProvider));

            LastUpdated = value.LastRefresh.ToLocalTime().ToString("g");
            UpdatedLabel = $"Updated {value.LastRefresh.ToLocalTime():t}";
        });
    }

    public void OnError(Exception error)
    {
    }

    public void OnCompleted()
    {
    }

    public void Dispose()
    {
        _costLoad?.Cancel();
        _subscription.Dispose();
    }
}
