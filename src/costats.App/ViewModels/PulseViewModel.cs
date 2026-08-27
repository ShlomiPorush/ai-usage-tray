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
    private string updatedLabel = "Updated never";

    /// <summary>True while the widget shows the all-accounts overview; false in single-account detail.</summary>
    [ObservableProperty]
    private bool isOverview = true;

    /// <summary>Mirrors AppSettings.ShowOverviewResetTimes for the overview cards.</summary>
    [ObservableProperty]
    private bool showResetTimes;

    /// <summary>
    /// True after the first-run window was deliberately dismissed, so the
    /// overview keeps a compact path back into setup.
    /// </summary>
    [ObservableProperty]
    private bool showOnboardingFallback;

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
        ShowResetTimes = _settings.ShowOverviewResetTimes;
        RemoteViewLink = _settings.RemoteViewShareLink;
        ShowOnboardingFallback = _settings.ShouldShowOnboardingFallback;
    }

    /// <summary>Applies onboarding changes immediately, without waiting for a pulse.</summary>
    public void NotifyOnboardingStateChanged() => ApplySettings();

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
            IsRefreshing = value.IsRefreshing;

            // Only update provider data if we have providers (keep last state during refresh)
            if (value.Providers.Count > 0)
            {
                // ── Build all data in local variables first (no UI mutations yet) ──
                var newProviders = new List<ProviderPulseViewModel>();
                var displayNames = CurrentDisplayNames();

                foreach (var (providerId, reading) in value.Providers)
                {
                    var displayName = displayNames.TryGetValue(providerId, out var name) ? name : providerId;
                    var vm = ProviderPulseViewModel.FromReading(
                        reading,
                        displayName,
                        _settings.ShowRemainingPercentages);

                    if (providerId.Equals("copilot", StringComparison.OrdinalIgnoreCase) && !_settings.CopilotEnabled)
                    {
                        continue;
                    }

                    // Z.AI without an API key is just noise - hide it entirely.
                    if (providerId.Equals("zai", StringComparison.OrdinalIgnoreCase) && !_settings.HasZaiKey)
                    {
                        continue;
                    }

                    newProviders.Add(vm);
                }

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
            }

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
