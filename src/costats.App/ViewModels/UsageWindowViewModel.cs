using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using costats.App.Controls;
using costats.Application.Analytics;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Analytics;
using costats.Core.Pulse;
using costats.Infrastructure.Analytics;
using costats.Infrastructure.Providers;
using Serilog;

namespace costats.App.ViewModels;

/// <summary>One monitored account in the Usage filter.</summary>
/// <param name="ProviderId">Pulse id such as "codex:openai-1"; null means every account.</param>
/// <param name="DisplayName">User-facing account nickname.</param>
/// <param name="AnalyticsAccountIds">Local-log buckets; empty for quota-only providers.</param>
/// <param name="ProviderKind">Provider family: claude, codex, zai, or copilot.</param>
public sealed record UsageAccountOption(
    string? ProviderId,
    string DisplayName,
    IReadOnlyList<string> AnalyticsAccountIds,
    string ProviderKind);

/// <summary>A provider's line in the hero block: cost, share bar and caption.</summary>
/// <param name="Name">User-facing provider name.</param>
/// <param name="ProviderKey">"claude" or "codex", the key the XAML colours by.</param>
/// <param name="ValueText">Formatted cost, or "unpriced".</param>
/// <param name="Share">0..1 share of the total, for the bar width.</param>
/// <param name="Caption">"88.9% of cost | 11.2B tokens".</param>
public sealed record UsageProviderRow(
    string Name,
    string ProviderKey,
    string ValueText,
    double Share,
    string Caption);

/// <summary>One of the five headline tiles.</summary>
public sealed record UsageStatTile(string Caption, string Value, string Footnote);

/// <summary>One row of the breakdown table, in either MODEL or DAY mode.</summary>
/// <param name="Label">Model id or date.</param>
/// <param name="ProviderKey">"claude", "codex", or empty for a day row (no dot).</param>
public sealed record UsageBreakdownRow(
    string Label,
    string ProviderKey,
    string ValueText,
    string ShareText,
    string TokensText);

/// <summary>
/// Drives the Usage window: asks <see cref="IUsageAnalyticsService"/> for a
/// report and turns it into text, bars and chart series.
/// </summary>
/// <remarks>
/// Every scan runs on the thread pool (<see cref="Task.Run(Func{Task})"/>), so
/// the first open of a large log set never freezes the window. The service
/// caches a scan for a couple of minutes, which makes range, metric and filter
/// changes instant; only the refresh button invalidates it.
/// </remarks>
public sealed partial class UsageWindowViewModel : ObservableObject
{
    /// <summary>Ranges the segmented control offers, in days.</summary>
    public static readonly int[] RangeChoices = [7, 30, 90];

    private readonly IUsageAnalyticsService _analytics;
    private readonly IPulseOrchestrator _orchestrator;
    private readonly AppSettings _settings;
    private readonly IEnumerable<ISignalSource> _staticSources;
    private readonly IAccountSourceRegistry _accountSources;
    private readonly IZaiModelUsageClient _zaiModelUsage;
    private CancellationTokenSource? _inFlight;
    private UsageReport? _report;
    private DateOnly _from;
    private DateOnly _to;
    private bool _suppressReload;
    private string? _pendingAccountId;
    private string? _selectedProviderId;

    /// <summary>Creates the view model over the app's analytics service.</summary>
    public UsageWindowViewModel(
        IUsageAnalyticsService analytics,
        IPulseOrchestrator orchestrator,
        AppSettings settings,
        IEnumerable<ISignalSource> staticSources,
        IAccountSourceRegistry accountSources,
        IZaiModelUsageClient zaiModelUsage)
    {
        ArgumentNullException.ThrowIfNull(analytics);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(staticSources);
        ArgumentNullException.ThrowIfNull(accountSources);
        ArgumentNullException.ThrowIfNull(zaiModelUsage);
        _analytics = analytics;
        _orchestrator = orchestrator;
        _settings = settings;
        _staticSources = staticSources;
        _accountSources = accountSources;
        _zaiModelUsage = zaiModelUsage;

        var today = DateOnly.FromDateTime(DateTime.Now);
        _from = today.AddDays(-(RangeDays - 1));
        _to = today;
        rangeLabel = UsageNumberFormat.RangeLabel(_from, _to);
        accounts = [AllAccountsOption];
        selectedAccount = AllAccountsOption;
        tiles = EmptyTiles();
    }

    private static UsageAccountOption AllAccountsOption => new(null, "All accounts", [], string.Empty);

    /// <summary>The account filter, "All accounts" first.</summary>
    [ObservableProperty]
    private IReadOnlyList<UsageAccountOption> accounts;

    /// <summary>The picked filter entry.</summary>
    [ObservableProperty]
    private UsageAccountOption? selectedAccount;

    /// <summary>Days in the selected range: 7, 30 or 90.</summary>
    [ObservableProperty]
    private int rangeDays = 30;

    /// <summary>Chart metric: 0 is cost, 1 is processed tokens.</summary>
    [ObservableProperty]
    private int metricIndex;

    /// <summary>Breakdown mode: 0 is per model, 1 is per day.</summary>
    [ObservableProperty]
    private int breakdownIndex;

    /// <summary>True while a scan or aggregation is running.</summary>
    [ObservableProperty]
    private bool isLoading;

    /// <summary>"Jul 25 to Aug 23".</summary>
    [ObservableProperty]
    private string rangeLabel;

    /// <summary>The hero figure, always exact to the cent.</summary>
    [ObservableProperty]
    private string totalCostText = "$0.00";

    [ObservableProperty]
    private string heroLabelText = "RAW TOKEN COST";

    [ObservableProperty]
    private bool showsRawCostHero = true;

    [ObservableProperty]
    private bool isCostMetricAvailable = true;

    [ObservableProperty]
    private bool isTokenOnlyHistory;

    /// <summary>"excludes 4 unpriced models", or empty when everything is priced.</summary>
    [ObservableProperty]
    private string unpricedNote = string.Empty;

    /// <summary>Per-provider hero rows, most expensive first.</summary>
    [ObservableProperty]
    private IReadOnlyList<UsageProviderRow> providerRows = [];

    /// <summary>Latest account-specific subscription quotas, all accounts or the selected one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuotaCards))]
    private IReadOnlyList<ProviderPulseViewModel> quotaCards = [];

    public bool HasQuotaCards => QuotaCards.Count > 0;

    [ObservableProperty]
    private string quotaHeaderText = "Quota usage";

    /// <summary>Explains when local cost history is broader than the selected account.</summary>
    [ObservableProperty]
    private string analyticsScopeNote = string.Empty;

    /// <summary>The five headline tiles.</summary>
    [ObservableProperty]
    private IReadOnlyList<UsageStatTile> tiles;

    /// <summary>The breakdown table's current rows.</summary>
    [ObservableProperty]
    private IReadOnlyList<UsageBreakdownRow> breakdownRows = [];

    /// <summary>What the chart control draws.</summary>
    [ObservableProperty]
    private UsageChartData chart = UsageChartData.Empty;

    /// <summary>"Daily cost" or "Daily tokens".</summary>
    [ObservableProperty]
    private string chartTitle = "Daily cost";

    /// <summary>Header of the breakdown table's first column.</summary>
    [ObservableProperty]
    private string breakdownColumnHeader = "Model";

    /// <summary>Header of the breakdown table's value column.</summary>
    [ObservableProperty]
    private string breakdownValueHeader = "Cost";

    /// <summary>A single line explaining an empty or failed report.</summary>
    [ObservableProperty]
    private string statusText = string.Empty;

    /// <summary>True once a report with at least one request has arrived.</summary>
    [ObservableProperty]
    private bool hasData;

    /// <summary>
    /// Loads the first report. Safe to call again: reopening the window just
    /// re-reads the cached scan.
    /// </summary>
    public Task InitializeAsync() => LoadAsync(invalidate: false);

    /// <summary>
    /// Loads the first report with the account filter already set to
    /// <paramref name="accountId"/>, for callers that arrive from one account's
    /// panel. The filter is applied as soon as the account list is known, so it
    /// survives the very first open when the picker is still empty. An id the
    /// engine does not know falls back to "All accounts".
    /// </summary>
    public Task InitializeForAccountAsync(string accountId)
    {
        _pendingAccountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId;
        return LoadAsync(invalidate: false);
    }

    /// <summary>Drops the cached scan and reads the logs again.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(invalidate: true);

    partial void OnRangeDaysChanged(int value) => Reload();

    partial void OnSelectedAccountChanged(UsageAccountOption? value)
    {
        if (value is null)
        {
            RestoreSelection();
            return;
        }

        _selectedProviderId = value.ProviderId;
        Reload();
    }

    /// <summary>
    /// Puts the picker back on the entry it is meant to be showing.
    /// </summary>
    /// <remarks>
    /// Replacing the item list makes the ComboBox drop its selection and write a
    /// null back through the two-way binding, sometimes a layout pass later. The
    /// picker has no "nothing selected" state ("All accounts" is the floor), so
    /// taking that null at face value would silently widen a view that was
    /// opened for one account into every account.
    /// </remarks>
    private void RestoreSelection()
    {
        if (Accounts.Count == 0)
        {
            return;
        }

        var restored = Accounts.FirstOrDefault(option => option.ProviderId == _selectedProviderId) ?? Accounts[0];
        var suppressed = _suppressReload;
        _suppressReload = true;
        try
        {
            SelectedAccount = restored;
        }
        finally
        {
            _suppressReload = suppressed;
        }
    }

    partial void OnMetricIndexChanged(int value)
    {
        ChartTitle = value == 1 ? "Daily tokens" : "Daily cost";
        if (_report is not null)
        {
            Chart = BuildChart(_report);
        }
    }

    partial void OnBreakdownIndexChanged(int value)
    {
        BreakdownColumnHeader = value == 1 ? "Day" : "Model";
        BreakdownValueHeader = IsTokenOnlyHistory ? "Tokens" : "Cost";
        if (_report is not null)
        {
            BreakdownRows = BuildBreakdown(_report);
        }
    }

    private void Reload()
    {
        if (_suppressReload)
        {
            return;
        }

        _ = LoadAsync(invalidate: false);
    }

    private async Task LoadAsync(bool invalidate)
    {
        // The previous request is cancelled but not disposed here: only the
        // call that owns a token source disposes it, in its own finally.
        _inFlight?.Cancel();
        var cts = new CancellationTokenSource();
        _inFlight = cts;
        var token = cts.Token;

        IsLoading = true;
        StatusText = HasData ? StatusText : "Reading the local agent logs...";

        var today = DateOnly.FromDateTime(DateTime.Now);
        var days = RangeDays;
        var range = UsageDateRange.LastDays(days, today);

        try
        {
            if (invalidate)
            {
                _analytics.Invalidate();
                await _orchestrator.RefreshOnceAsync(RefreshTrigger.Manual, token).ConfigureAwait(true);
            }
            else if (_orchestrator.CurrentState is null)
            {
                await _orchestrator.RefreshOnceAsync(RefreshTrigger.Silent, token).ConfigureAwait(true);
            }

            var accountList = await Task.Run(() => _analytics.GetAccountsAsync(token), token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            // Read after the picker is applied: a caller that asked for one
            // account's view moves the selection here, and the report has to be
            // the one the picker now claims to show.
            ApplyAccounts(accountList);
            ApplyQuotaCards();
            var selected = SelectedAccount;

            if (selected?.ProviderKind == "zai")
            {
                var history = await _zaiModelUsage.FetchModelUsageAsync(
                    _settings.ZAiCodingApiKey,
                    _settings.ZAiApiKey,
                    range.From ?? today,
                    range.To ?? today,
                    token).ConfigureAwait(true);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _from = range.From ?? today;
                _to = range.To ?? today;
                ApplyZaiHistory(history);
                return;
            }

            if (selected?.ProviderId is not null && selected.AnalyticsAccountIds.Count == 0)
            {
                ApplyQuotaOnly(selected);
                return;
            }

            AnalyticsScopeNote = selected?.ProviderKind == MonitoredAccountSettings.CodexType
                ? "Token and raw-cost history includes all Codex accounts because Codex stores local rollouts in one shared log set."
                : string.Empty;

            IReadOnlyCollection<string>? filter = selected?.ProviderId is null
                ? null
                : selected.AnalyticsAccountIds;
            var report = await Task.Run(() => _analytics.GetReportAsync(range, filter, token), token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _from = range.From ?? today;
            _to = range.To ?? today;
            Apply(report);
        }
        catch (OperationCanceledException)
        {
            // A newer request replaced this one; its result is the live one.
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Usage report failed");
            _report = null;
            HasData = false;
            StatusText = "Could not read the local agent logs. See the log file for details.";
        }
        finally
        {
            if (ReferenceEquals(_inFlight, cts))
            {
                IsLoading = false;
                _inFlight = null;
            }

            cts.Dispose();
        }
    }

    private void ApplyAccounts(IReadOnlyList<UsageAccountInfo> discovered)
    {
        var profiles = _staticSources
            .Concat(_accountSources.Current)
            .Select(source => source.Profile)
            .Where(profile => ShouldInclude(profile.ProviderId))
            .ToList();

        var catalog = UsageAccountCatalog.Build(profiles, discovered);
        var options = new List<UsageAccountOption>(catalog.Count + 1) { AllAccountsOption };
        options.AddRange(catalog.Select(account => new UsageAccountOption(
            account.ProviderId,
            account.DisplayName,
            account.AnalyticsAccountIds,
            account.ProviderKind)));

        // A one-shot request from another window wins over whatever the picker
        // was left on; it is consumed here whether or not it matches an account.
        var requested = _pendingAccountId;
        _pendingAccountId = null;

        var sameList = Accounts.Count == options.Count &&
            Accounts.Zip(options).All(pair => pair.First == pair.Second);

        var wanted = requested is null
            ? SelectedAccount?.ProviderId
            : ResolveRequestedProviderId(options, requested);

        if (sameList && wanted == SelectedAccount?.ProviderId)
        {
            return;
        }

        _suppressReload = true;
        try
        {
            if (!sameList)
            {
                Accounts = options;
            }

            // Always pick out of the live list, so the picker's selection is an
            // item it actually holds.
            var live = Accounts;
            SelectedAccount = live.FirstOrDefault(option => option.ProviderId == wanted) ?? live[0];
        }
        finally
        {
            _suppressReload = false;
        }
    }

    private bool ShouldInclude(string providerId)
    {
        var kind = UsageAccountCatalog.ProviderKind(providerId);
        return kind switch
        {
            "copilot" => _settings.CopilotEnabled,
            "zai" => _settings.HasZaiKey,
            _ => true
        };
    }

    private static string? ResolveRequestedProviderId(
        IReadOnlyList<UsageAccountOption> options,
        string requested)
    {
        var exact = options.FirstOrDefault(option =>
            string.Equals(option.ProviderId, requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.ProviderId;
        }

        // Backward compatibility for old callers that passed an analytics id.
        // Only use it when the mapping is unambiguous; merged Codex deliberately
        // has two account choices and must not silently pick one.
        var analyticsMatches = options
            .Where(option => option.AnalyticsAccountIds.Any(id =>
                string.Equals(id, requested, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return analyticsMatches.Count == 1 ? analyticsMatches[0].ProviderId : null;
    }

    private void ApplyQuotaCards()
    {
        var state = _orchestrator.CurrentState;
        var selectedId = SelectedAccount?.ProviderId;
        var candidates = Accounts
            .Where(option => option.ProviderId is not null &&
                (selectedId is null || string.Equals(option.ProviderId, selectedId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        QuotaCards = candidates.Select(option =>
        {
            if (state?.Providers.TryGetValue(option.ProviderId!, out var reading) == true)
            {
                return ProviderPulseViewModel.FromReading(reading, option.DisplayName);
            }

            return new ProviderPulseViewModel
            {
                ProviderId = option.ProviderId!,
                DisplayName = option.DisplayName,
                StatusSummary = "No quota data available yet"
            };
        }).ToList();

        QuotaHeaderText = SelectedAccount?.ProviderId is null
            ? "Quota usage"
            : $"{SelectedAccount.DisplayName} quota";
    }

    private void ApplyQuotaOnly(UsageAccountOption selected)
    {
        _report = null;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var range = UsageDateRange.LastDays(RangeDays, today);
        _from = range.From ?? today;
        _to = range.To ?? today;
        RangeLabel = UsageNumberFormat.RangeLabel(_from, _to);
        HasData = false;
        AnalyticsScopeNote = string.Empty;
        ProviderRows = [];
        Tiles = EmptyTiles();
        Chart = UsageChartData.Empty;
        BreakdownRows = [];
        TotalCostText = "$0.00";
        HeroLabelText = "RAW TOKEN COST";
        ShowsRawCostHero = true;
        IsCostMetricAvailable = true;
        IsTokenOnlyHistory = false;
        UnpricedNote = string.Empty;
        StatusText = selected.ProviderKind == "zai"
            ? "GLM quota usage is shown above. No GLM model usage was returned for this range."
            : $"{selected.DisplayName} quota usage is shown above. No local token history is available for this provider.";
    }

    private void ApplyZaiHistory(ZaiModelUsageSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.TotalTokens <= 0)
        {
            ApplyQuotaOnly(SelectedAccount!);
            return;
        }

        Apply(BuildZaiReport(snapshot));
    }

    private static UsageReport BuildZaiReport(ZaiModelUsageSnapshot snapshot)
    {
        var models = snapshot.Models.Count > 0
            ? snapshot.Models
            : [new ZaiModelUsageSeries("GLM", snapshot.TokensByDay, snapshot.TotalTokens)];

        var daily = new List<DailyUsage>();
        for (var index = 0; index < snapshot.Days.Count; index++)
        {
            var tokens = index < snapshot.TokensByDay.Count ? snapshot.TokensByDay[index] : 0;
            var calls = index < snapshot.CallsByDay.Count ? snapshot.CallsByDay[index] : 0;
            if (tokens > 0 || calls > 0)
            {
                daily.Add(new DailyUsage(snapshot.Days[index], ZaiTotals(tokens, calls)));
            }
        }

        var dailyByModel = new List<DailyModelUsage>();
        foreach (var model in models)
        {
            for (var index = 0; index < snapshot.Days.Count && index < model.TokensByDay.Count; index++)
            {
                var tokens = model.TokensByDay[index];
                if (tokens > 0)
                {
                    dailyByModel.Add(new DailyModelUsage(
                        snapshot.Days[index],
                        UsageProviderKind.Zai,
                        model.ModelName,
                        ZaiTotals(tokens, 0)));
                }
            }
        }

        var modelRows = models
            .Where(model => model.TotalTokens > 0)
            .OrderByDescending(model => model.TotalTokens)
            .Select(model => new ModelUsage(
                model.ModelName,
                UsageProviderKind.Zai,
                IsPriced: false,
                ZaiTotals(model.TotalTokens, 0)))
            .ToList();
        var totals = ZaiTotals(snapshot.TotalTokens, snapshot.TotalCalls);

        return new UsageReport
        {
            Range = snapshot.Days.Count == 0
                ? UsageDateRange.All
                : new UsageDateRange(snapshot.Days[0], snapshot.Days[^1]),
            TimeZoneId = TimeZoneInfo.Local.Id,
            AccountFilter = ["zai"],
            FirstDay = daily.FirstOrDefault()?.Day,
            LastDay = daily.LastOrDefault()?.Day,
            Daily = daily,
            DailyByModel = dailyByModel,
            ByModel = modelRows,
            ByProvider = [new ProviderUsage(UsageProviderKind.Zai, totals)],
            ByAccount = [new AccountUsage("zai", UsageProviderKind.Zai, totals)],
            Totals = totals,
            UnpricedModels = modelRows.Select(model => model.Model).ToList(),
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    private static UsageTotals ZaiTotals(long tokens, long calls) => new()
    {
        Tokens = new UsageTokens { UncachedInputTokens = Math.Max(0, tokens) },
        RequestCount = Math.Max(0, calls),
        UnpricedTokens = Math.Max(0, tokens)
    };

    private void Apply(UsageReport report)
    {
        _report = report;
        RangeLabel = UsageNumberFormat.RangeLabel(_from, _to);
        HasData = !report.IsEmpty;
        StatusText = report.IsEmpty
            ? "No local agent usage in this range."
            : string.Empty;

        var totals = report.Totals;
        var tokenOnly = report.ByProvider.Count == 1 && report.ByProvider[0].Provider == UsageProviderKind.Zai;
        IsTokenOnlyHistory = tokenOnly;
        IsCostMetricAvailable = !tokenOnly;
        ShowsRawCostHero = !tokenOnly;
        HeroLabelText = tokenOnly ? "MODEL TOKENS" : "RAW TOKEN COST";
        if (tokenOnly && MetricIndex != 1)
        {
            MetricIndex = 1;
        }

        TotalCostText = tokenOnly
            ? UsageNumberFormat.Tokens(totals.Tokens.ProcessedTokens)
            : UsageNumberFormat.Money(totals.CostUsd);

        // Unpriced tokens are counted but not costed, so the hero figure is a
        // floor, not the truth. Say so instead of implying those models are free.
        UnpricedNote = tokenOnly
            ? "Z.AI reports aggregate model tokens, not an input/output split or raw API cost."
            : totals.UnpricedTokens > 0 && report.UnpricedModels.Count > 0
            ? $"excludes {report.UnpricedModels.Count} unpriced model{(report.UnpricedModels.Count == 1 ? string.Empty : "s")}"
            : string.Empty;

        ProviderRows = BuildProviderRows(report);
        Tiles = tokenOnly ? BuildZaiTiles(report) : BuildTiles(report);
        BreakdownValueHeader = tokenOnly ? "Tokens" : "Cost";
        Chart = BuildChart(report);
        BreakdownRows = BuildBreakdown(report);
    }

    private static IReadOnlyList<UsageProviderRow> BuildProviderRows(UsageReport report)
    {
        var totalCost = report.Totals.CostUsd;
        var totalTokens = report.Totals.Tokens.ProcessedTokens;

        return report.ByProvider
            .OrderByDescending(provider => provider.Totals.CostUsd)
            .ThenByDescending(provider => provider.Totals.Tokens.ProcessedTokens)
            .Select(provider =>
            {
                var tokens = provider.Totals.Tokens.ProcessedTokens;
                var share = totalCost > 0m
                    ? (double)(provider.Totals.CostUsd / totalCost)
                    : (totalTokens > 0 ? (double)tokens / totalTokens : 0d);

                var caption = totalCost > 0m
                    ? $"{UsageNumberFormat.Percent(provider.Totals.CostUsd, totalCost)} of cost · {UsageNumberFormat.Tokens(tokens)} tokens"
                    : $"{UsageNumberFormat.Percent(tokens, totalTokens)} of tokens · {UsageNumberFormat.Tokens(tokens)} tokens";

                return new UsageProviderRow(
                    DisplayName(provider.Provider),
                    Key(provider.Provider),
                    provider.Provider == UsageProviderKind.Zai
                        ? UsageNumberFormat.Tokens(tokens)
                        : Cost(provider.Totals),
                    share,
                    caption);
            })
            .ToList();
    }

    private static IReadOnlyList<UsageStatTile> BuildTiles(UsageReport report)
    {
        var tokens = report.Totals.Tokens;
        var activeDays = Math.Max(1, report.Daily.Count);
        var perActiveDay = tokens.ProcessedTokens / activeDays;

        return
        [
            new UsageStatTile(
                "Processed tokens",
                UsageNumberFormat.Tokens(tokens.ProcessedTokens),
                $"{UsageNumberFormat.Tokens(perActiveDay)} per active day"),
            new UsageStatTile(
                "Cached input",
                UsageNumberFormat.Tokens(tokens.CacheReadInputTokens),
                $"{UsageNumberFormat.Percent(tokens.CacheReadInputTokens, tokens.InputTokens)} of observed input"),
            new UsageStatTile(
                "Uncached input",
                UsageNumberFormat.Tokens(tokens.UncachedInputTokens),
                $"{UsageNumberFormat.Tokens(tokens.CacheWriteInputTokens)} cache writes"),
            new UsageStatTile(
                "Output",
                UsageNumberFormat.Tokens(tokens.OutputTokens),
                $"includes {UsageNumberFormat.Tokens(tokens.ReasoningOutputTokens)} reasoning"),
            new UsageStatTile(
                "Cache savings",
                UsageNumberFormat.Money(report.Totals.CacheSavingsUsd),
                $"{UsageNumberFormat.Multiplier(report.Totals.CacheSavingsUsd, report.Totals.CostUsd)} the raw token cost")
        ];
    }

    private static IReadOnlyList<UsageStatTile> BuildZaiTiles(UsageReport report)
    {
        var tokens = report.Totals.Tokens.ProcessedTokens;
        var calls = report.Totals.RequestCount;
        var activeDays = Math.Max(1, report.Daily.Count(day => day.Totals.Tokens.ProcessedTokens > 0));
        var perCall = calls > 0 ? tokens / calls : 0;

        return
        [
            new UsageStatTile("Processed tokens", UsageNumberFormat.Tokens(tokens),
                $"{UsageNumberFormat.Tokens(tokens / activeDays)} per active day"),
            new UsageStatTile("Model calls", calls.ToString("N0"),
                $"{UsageNumberFormat.Tokens(perCall)} tokens per call"),
            new UsageStatTile("Active days", activeDays.ToString("N0"),
                $"of {report.Daily.Count} days returned"),
            new UsageStatTile("Models", report.ByModel.Count.ToString("N0"),
                report.ByModel.FirstOrDefault()?.Model ?? string.Empty),
            new UsageStatTile("Source", "Z.AI", "official model-usage endpoint")
        ];
    }

    private static IReadOnlyList<UsageStatTile> EmptyTiles() =>
    [
        new UsageStatTile("Processed tokens", "0", string.Empty),
        new UsageStatTile("Cached input", "0", string.Empty),
        new UsageStatTile("Uncached input", "0", string.Empty),
        new UsageStatTile("Output", "0", string.Empty),
        new UsageStatTile("Cache savings", "$0.00", string.Empty)
    ];

    private UsageChartData BuildChart(UsageReport report)
    {
        var days = new List<DateOnly>();
        for (var day = _from; day <= _to; day = day.AddDays(1))
        {
            days.Add(day);
        }

        if (days.Count == 0)
        {
            return UsageChartData.Empty;
        }

        var index = days
            .Select((day, position) => (day, position))
            .ToDictionary(entry => entry.day, entry => entry.position);

        var useTokens = MetricIndex == 1;
        var series = new List<UsageChartSeries>();
        foreach (var provider in report.ByProvider.Select(entry => entry.Provider).Order())
        {
            var values = new double[days.Count];
            foreach (var bucket in report.DailyByModel.Where(entry => entry.Provider == provider))
            {
                if (index.TryGetValue(bucket.Day, out var position))
                {
                    values[position] += useTokens
                        ? bucket.Totals.Tokens.ProcessedTokens
                        : (double)bucket.Totals.CostUsd;
                }
            }

            series.Add(new UsageChartSeries(provider, values));
        }

        return new UsageChartData
        {
            Days = days,
            Series = series,
            AxisLabel = useTokens
                ? value => UsageNumberFormat.AxisTokens((long)Math.Round(value))
                : value => UsageNumberFormat.AxisCost((decimal)value)
        };
    }

    private IReadOnlyList<UsageBreakdownRow> BuildBreakdown(UsageReport report)
    {
        var totalCost = report.Totals.CostUsd;
        var totalTokens = report.Totals.Tokens.ProcessedTokens;
        var tokenOnly = report.ByProvider.Count == 1 && report.ByProvider[0].Provider == UsageProviderKind.Zai;

        if (BreakdownIndex == 1)
        {
            return report.Daily
                .OrderByDescending(day => day.Day)
                .Select(day => new UsageBreakdownRow(
                    UsageNumberFormat.LongDayLabel(day.Day),
                    string.Empty,
                    tokenOnly ? UsageNumberFormat.Tokens(day.Totals.Tokens.ProcessedTokens) : Cost(day.Totals),
                    tokenOnly
                        ? UsageNumberFormat.Percent(day.Totals.Tokens.ProcessedTokens, totalTokens)
                        : UsageNumberFormat.Percent(day.Totals.CostUsd, totalCost),
                    UsageNumberFormat.Tokens(day.Totals.Tokens.ProcessedTokens)))
                .ToList();
        }

        return report.ByModel
            .Select(model => new UsageBreakdownRow(
                model.Model,
                Key(model.Provider),
                tokenOnly
                    ? UsageNumberFormat.Tokens(model.Totals.Tokens.ProcessedTokens)
                    : model.IsPriced ? UsageNumberFormat.Money(model.Totals.CostUsd) : "unpriced",
                tokenOnly
                    ? UsageNumberFormat.Percent(model.Totals.Tokens.ProcessedTokens, totalTokens)
                    : UsageNumberFormat.Percent(model.Totals.CostUsd, totalCost),
                UsageNumberFormat.Tokens(model.Totals.Tokens.ProcessedTokens)))
            .ToList();
    }

    /// <summary>
    /// A bucket's cost, or "unpriced" when it consumed tokens no price covers.
    /// A bucket that mixes priced and unpriced models still shows its priced
    /// cost: the hero footnote is what warns that the figure is a floor.
    /// </summary>
    private static string Cost(UsageTotals totals) =>
        UsageNumberFormat.CostOrUnpriced(totals.CostUsd, totals.UnpricedTokens);

    private static string DisplayName(UsageProviderKind provider) => provider switch
    {
        UsageProviderKind.Claude => "Claude Code",
        UsageProviderKind.Zai => "GLM",
        _ => "Codex (all accounts)"
    };

    private static string Key(UsageProviderKind provider) => provider switch
    {
        UsageProviderKind.Claude => "claude",
        UsageProviderKind.Zai => "zai",
        _ => "codex"
    };
}
