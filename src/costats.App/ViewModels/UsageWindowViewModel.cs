using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using costats.App.Controls;
using costats.Core.Analytics;
using costats.Infrastructure.Analytics;
using Serilog;

namespace costats.App.ViewModels;

/// <summary>One entry of the account filter. A null id means "every account".</summary>
public sealed record UsageAccountOption(string? AccountId, string DisplayName);

/// <summary>A provider's line in the hero block: cost, share bar and caption.</summary>
/// <param name="Name">User-facing provider name.</param>
/// <param name="ProviderKey">"claude" or "codex", the key the XAML colours by.</param>
/// <param name="ValueText">Formatted cost, or "unpriced".</param>
/// <param name="Share">0..1 share of the total, for the bar width.</param>
/// <param name="Caption">"88.9% of cost | 11.2B tokens".</param>
/// <param name="DeltaText">Change against the comparison period, or empty.</param>
public sealed record UsageProviderRow(
    string Name,
    string ProviderKey,
    string ValueText,
    double Share,
    string Caption,
    string DeltaText = "");

/// <summary>One of the five headline tiles.</summary>
public sealed record UsageStatTile(string Caption, string Value, string Footnote, string DeltaText = "");

/// <summary>One row of the breakdown table, in either MODEL or DAY mode.</summary>
/// <param name="Label">Model id or date.</param>
/// <param name="ProviderKey">"claude", "codex", or empty for a day row (no dot).</param>
/// <param name="ChangeText">Change against the comparison period, or empty.</param>
/// <param name="PreviousValueText">The row's value in comparison period B, or empty.</param>
/// <param name="PreviousTokensText">The row's tokens in comparison period B, or empty.</param>
/// <param name="DrillDay">The day a day row opens by hour, or null when the row cannot be opened.</param>
public sealed record UsageBreakdownRow(
    string Label,
    string ProviderKey,
    string ValueText,
    string ShareText,
    string TokensText,
    string ChangeText = "",
    DateOnly? DrillDay = null,
    string PreviousValueText = "",
    string PreviousTokensText = "")
{
    /// <summary>True when clicking the row opens its day by hour.</summary>
    public bool CanDrill => DrillDay is not null;

    /// <summary>What a screen reader announces for an openable row.</summary>
    public string DrillName => CanDrill ? $"Show {Label} by hour" : Label;
}

/// <summary>
/// Drives the Usage window: asks <see cref="IUsageAnalyticsService"/> for a
/// report, and optionally a comparison report, and turns them into text, bars
/// and chart series.
/// </summary>
/// <remarks>
/// Every scan runs on the thread pool (<see cref="Task.Run(Func{Task})"/>), so
/// the first open of a large log set never freezes the window. The service
/// caches a scan for a couple of minutes, which makes range, metric and filter
/// changes instant; only the refresh button invalidates it. Nothing here is
/// persisted: the range, comparison and sort live for the app session.
/// </remarks>
public sealed partial class UsageWindowViewModel : ObservableObject
{
    /// <summary>
    /// Preset ranges the segmented control offers, in days. <see cref="PastDayRange"/>
    /// is the rolling past 24 hours with hourly buckets, not a calendar day.
    /// </summary>
    public static readonly int[] RangeChoices = [PastDayRange, 7, 30, 90];

    /// <summary>The <see cref="RangeDays"/> value that means "Past 24h".</summary>
    public const int PastDayRange = 1;

    /// <summary>The <see cref="RangeDays"/> value that means a custom range.</summary>
    public const int CustomRange = 0;

    private readonly IUsageAnalyticsService _analytics;
    private CancellationTokenSource? _inFlight;
    private LoadedView? _view;
    private bool _suppressReload;
    private bool _applyingState;
    private string? _pendingAccountId;
    private string? _selectedAccountId;
    private DateOnly _customFrom;
    private DateOnly _customTo;
    private UsagePeriod? _comparisonRange;
    private UsageSortState _modelSort = UsageSortState.ModelDefault;
    private UsageSortState _timeSort = UsageSortState.TimeDefault;
    private ViewState? _backState;

    /// <summary>Creates the view model over the app's analytics service.</summary>
    public UsageWindowViewModel(IUsageAnalyticsService analytics)
    {
        ArgumentNullException.ThrowIfNull(analytics);
        _analytics = analytics;

        var today = Today;
        _customFrom = today;
        _customTo = today;
        var period = UsagePeriod.LastDays(RangeDays, today);
        rangeLabel = UsageNumberFormat.RangeLabel(period.From, period.To, today);
        accounts = [AllAccountsOption];
        selectedAccount = AllAccountsOption;
        tiles = EmptyTiles();
        UpdateSortGlyphs();
    }

    private static UsageAccountOption AllAccountsOption => new(null, "All accounts");

    /// <summary>The account filter, "All accounts" first.</summary>
    [ObservableProperty]
    private IReadOnlyList<UsageAccountOption> accounts;

    /// <summary>The picked filter entry.</summary>
    [ObservableProperty]
    private UsageAccountOption? selectedAccount;

    /// <summary>
    /// The selected range: 1 (the past 24 hours), 7, 30 or 90 days, or
    /// <see cref="CustomRange"/> for the picked dates.
    /// </summary>
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

    /// <summary>"vs Jun 25 to Jul 24", or empty when not comparing.</summary>
    [ObservableProperty]
    private string comparisonLabel = string.Empty;

    /// <summary>True when the current report has a comparison report beside it.</summary>
    [ObservableProperty]
    private bool isComparing;

    /// <summary>Caveats about the comparison, or empty.</summary>
    [ObservableProperty]
    private string comparisonNote = string.Empty;

    /// <summary>The hero figure, always exact to the cent.</summary>
    [ObservableProperty]
    private string totalCostText = "$0.00";

    /// <summary>The hero figure's change against the comparison period, or empty.</summary>
    [ObservableProperty]
    private string totalDeltaText = string.Empty;

    /// <summary>"excludes 4 unpriced models", or empty when everything is priced.</summary>
    [ObservableProperty]
    private string unpricedNote = string.Empty;

    /// <summary>Per-provider hero rows, most expensive first.</summary>
    [ObservableProperty]
    private IReadOnlyList<UsageProviderRow> providerRows = [];

    /// <summary>The five headline tiles.</summary>
    [ObservableProperty]
    private IReadOnlyList<UsageStatTile> tiles;

    /// <summary>The breakdown table's current rows.</summary>
    [ObservableProperty]
    private IReadOnlyList<UsageBreakdownRow> breakdownRows = [];

    /// <summary>What the chart control draws.</summary>
    [ObservableProperty]
    private UsageChartData chart = UsageChartData.Empty;

    /// <summary>"Daily API value", "Hourly tokens" and so on.</summary>
    [ObservableProperty]
    private string chartTitle = "Daily API value";

    /// <summary>Label of the breakdown's time toggle: "DAY", or "HOUR" for an hourly period.</summary>
    [ObservableProperty]
    private string timeBreakdownLabel = "DAY";

    /// <summary>Header of the breakdown table's first column.</summary>
    [ObservableProperty]
    private string breakdownColumnHeader = "Model";

    /// <summary>Header of the breakdown table's value column.</summary>
    [ObservableProperty]
    private string breakdownValueHeader = "API value";

    /// <summary>A hint beside the breakdown title, such as how to open a day.</summary>
    [ObservableProperty]
    private string breakdownHint = string.Empty;

    /// <summary>
    /// True when the breakdown shows period B's value and tokens and the
    /// change beside period A's, in place of the share column.
    /// </summary>
    [ObservableProperty]
    private bool showComparisonColumns;

    /// <summary>Header of the breakdown table's tokens column.</summary>
    [ObservableProperty]
    private string breakdownTokensHeader = "Tokens";

    /// <summary>"Change A → B": the change column runs from the earlier period to the later one.</summary>
    [ObservableProperty]
    private string changeHeader = "Change";

    /// <summary>Sort arrow of period B's value column, or empty.</summary>
    [ObservableProperty]
    private string previousValueSortGlyph = string.Empty;

    /// <summary>Sort arrow of period B's tokens column, or empty.</summary>
    [ObservableProperty]
    private string previousTokensSortGlyph = string.Empty;

    /// <summary>The hero figure for period B, or empty when not comparing.</summary>
    [ObservableProperty]
    private string totalPreviousText = string.Empty;

    /// <summary>Sort arrow of the first column, or empty.</summary>
    [ObservableProperty]
    private string labelSortGlyph = string.Empty;

    /// <summary>Sort arrow of the value column, or empty.</summary>
    [ObservableProperty]
    private string valueSortGlyph = string.Empty;

    /// <summary>Sort arrow of the share column, or empty.</summary>
    [ObservableProperty]
    private string shareSortGlyph = string.Empty;

    /// <summary>Sort arrow of the tokens column, or empty.</summary>
    [ObservableProperty]
    private string tokensSortGlyph = string.Empty;

    /// <summary>Sort arrow of the change column, or empty.</summary>
    [ObservableProperty]
    private string changeSortGlyph = string.Empty;

    /// <summary>True when a day was opened and Back returns to the range it came from.</summary>
    [ObservableProperty]
    private bool canGoBack;

    /// <summary>"Back to 30 days".</summary>
    [ObservableProperty]
    private string backLabel = string.Empty;

    /// <summary>A single line explaining an empty or failed report.</summary>
    [ObservableProperty]
    private string statusText = string.Empty;

    /// <summary>True once a report, or its comparison, has at least one request.</summary>
    [ObservableProperty]
    private bool hasData;

    /// <summary>True when the custom range is the one on screen.</summary>
    public bool IsCustomRange => RangeDays == CustomRange;

    /// <summary>Today in the analytics time zone.</summary>
    public DateOnly Today => UsagePeriods.Today(DateTimeOffset.Now, _analytics.TimeZone);

    /// <summary>The picked custom range's first day.</summary>
    public DateOnly CustomFrom => _customFrom;

    /// <summary>The picked custom range's last day.</summary>
    public DateOnly CustomTo => _customTo;

    /// <summary>The picked comparison days, or null for the previous period.</summary>
    public (DateOnly From, DateOnly To)? ComparisonRange =>
        _comparisonRange is { } range ? (range.From, range.To) : null;

    /// <summary>The first and last local day the selected range covers.</summary>
    public (DateOnly From, DateOnly To) SelectedDays()
    {
        var now = DateTimeOffset.Now;
        var zone = _analytics.TimeZone;
        var resolved = UsagePeriods.Resolve(CurrentPeriod(UsagePeriods.Today(now, zone)), now, zone);
        return (resolved.FirstDay, resolved.LastDay);
    }

    /// <summary>Days the selected range stands for; the past 24 hours count as one.</summary>
    public int SelectedDayCount => CurrentPeriod(Today).DayCount;

    /// <summary>The label of the selected range: "Sep 27 to Oct 26".</summary>
    public string DescribeSelectedRange()
    {
        var now = DateTimeOffset.Now;
        var zone = _analytics.TimeZone;
        var today = UsagePeriods.Today(now, zone);
        return Describe(UsagePeriods.Resolve(CurrentPeriod(today), now, zone), today, zone);
    }

    /// <summary>The first and last local day of the period just before the selected range.</summary>
    public (DateOnly From, DateOnly To) PreviousPeriodDays()
    {
        var now = DateTimeOffset.Now;
        var zone = _analytics.TimeZone;
        var previous = UsagePeriods.ResolveComparison(CurrentPeriod(UsagePeriods.Today(now, zone)), null, now, zone);
        return (previous.FirstDay, previous.LastDay);
    }

    /// <summary>The label of the previous period the selected range would be compared with.</summary>
    public string DescribePreviousPeriod()
    {
        var now = DateTimeOffset.Now;
        var zone = _analytics.TimeZone;
        var today = UsagePeriods.Today(now, zone);
        return Describe(UsagePeriods.ResolveComparison(CurrentPeriod(today), null, now, zone), today, zone);
    }

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

    /// <summary>
    /// Shows a custom range of whole local days. Returns false, changing
    /// nothing, when the range is outside what the picker allows.
    /// </summary>
    public bool ApplyCustomRange(DateOnly from, DateOnly to)
    {
        if (from > to)
        {
            (from, to) = (to, from);
        }

        if (!UsagePeriods.IsValidCustomRange(from, to, Today))
        {
            return false;
        }

        _backState = null;
        _applyingState = true;
        try
        {
            _customFrom = from;
            _customTo = to;
            RangeDays = CustomRange;
        }
        finally
        {
            _applyingState = false;
        }

        UpdateBack();
        Reload();
        return true;
    }

    /// <summary>
    /// Turns the comparison on, against the previous period when
    /// <paramref name="from"/> is null or against the picked days otherwise,
    /// or off. The picked days may be any length: changes then compare the
    /// average per day. Returns false, changing nothing, for days outside what
    /// the picker allows.
    /// </summary>
    public bool ApplyComparison(bool enabled, DateOnly? from = null, DateOnly? to = null)
    {
        if (!enabled)
        {
            _comparisonRange = null;
            if (IsComparing)
            {
                IsComparing = false;
                Reload();
            }

            return true;
        }

        UsagePeriod? picked = null;
        if (from is { } first)
        {
            var range = UsagePeriod.Days(first, to ?? first);
            if (!UsagePeriods.IsValidCustomRange(range.From, range.To, Today))
            {
                return false;
            }

            picked = range;
        }

        _comparisonRange = picked;
        IsComparing = true;
        Reload();
        return true;
    }

    /// <summary>
    /// Compares two freely picked periods in one step: period A becomes the
    /// range on screen and period B the comparison. A null A keeps the range
    /// on screen as it is; a null B compares with the period just before A.
    /// Returns false, changing nothing, when either period is outside what the
    /// picker allows.
    /// </summary>
    public bool ApplyComparisonPeriods((DateOnly From, DateOnly To)? current, (DateOnly From, DateOnly To)? comparison)
    {
        var today = Today;
        if ((current is { } a && !UsagePeriods.IsValidCustomRange(Min(a), Max(a), today)) ||
            (comparison is { } b && !UsagePeriods.IsValidCustomRange(Min(b), Max(b), today)))
        {
            return false;
        }

        _applyingState = true;
        try
        {
            if (current is { } range)
            {
                _backState = null;
                _customFrom = Min(range);
                _customTo = Max(range);
                RangeDays = CustomRange;
            }

            _comparisonRange = comparison is { } picked ? UsagePeriod.Days(picked.From, picked.To) : null;
            IsComparing = true;
        }
        finally
        {
            _applyingState = false;
        }

        UpdateBack();
        Reload();
        return true;

        static DateOnly Min((DateOnly From, DateOnly To) days) => days.From <= days.To ? days.From : days.To;
        static DateOnly Max((DateOnly From, DateOnly To) days) => days.From <= days.To ? days.To : days.From;
    }

    /// <summary>Drops the cached scan and reads the logs again.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(invalidate: true);

    /// <summary>Sorts the breakdown by a column, or reverses it when it already is.</summary>
    [RelayCommand]
    private void Sort(string? column)
    {
        if (!Enum.TryParse<UsageSortColumn>(column, ignoreCase: true, out var parsed))
        {
            return;
        }

        if (BreakdownIndex == 1)
        {
            _timeSort = _timeSort.Toggle(parsed, labelIsTime: true);
        }
        else
        {
            _modelSort = EffectiveModelSort().Toggle(parsed, labelIsTime: false);
        }

        UpdateSortGlyphs();
        if (_view is not null)
        {
            BreakdownRows = BuildBreakdown(_view);
        }
    }

    /// <summary>Opens a day row by hour, keeping the comparison on its parallel day.</summary>
    [RelayCommand]
    private void OpenRow(UsageBreakdownRow? row)
    {
        if (row?.DrillDay is not { } day || _view is not { } view || view.Primary.IsHourly)
        {
            return;
        }

        // The row belongs to the report on screen, which may not be the
        // selection any more while a newer load is running. The offset and
        // the Back target therefore come from that report, never from the
        // selection.
        int? shift = view.ComparisonPeriod is { } compared
            ? compared.FirstDay.DayNumber - view.Primary.FirstDay.DayNumber
            : null;

        // Sort and breakdown mode never wait for a load, so Back keeps the
        // ones on screen now.
        _backState = view.State with
        {
            BreakdownIndex = BreakdownIndex,
            ModelSort = _modelSort,
            TimeSort = _timeSort
        };
        _applyingState = true;
        try
        {
            _customFrom = day;
            _customTo = day;
            RangeDays = CustomRange;
            IsComparing = shift is not null;

            // Against the previous period the day is compared with its
            // matching day there, not with the day before it. Picked days stay
            // as they are, and the day is compared with their average day.
            _comparisonRange = view.State.ComparisonRange is { } picked
                ? picked
                : shift is { } days ? UsagePeriod.Day(day.AddDays(days)) : null;
        }
        finally
        {
            _applyingState = false;
        }

        UpdateBack();
        Reload();
    }

    /// <summary>Returns from an opened day to the range it was opened from.</summary>
    [RelayCommand]
    private void Back()
    {
        if (_backState is not { } state)
        {
            return;
        }

        _backState = null;
        _applyingState = true;
        try
        {
            _customFrom = state.CustomFrom;
            _customTo = state.CustomTo;
            _comparisonRange = state.ComparisonRange;
            _modelSort = state.ModelSort;
            _timeSort = state.TimeSort;
            IsComparing = state.IsComparing;
            RangeDays = state.RangeDays;
            BreakdownIndex = state.BreakdownIndex;
        }
        finally
        {
            _applyingState = false;
        }

        UpdateSortGlyphs();
        UpdateBack();
        Reload();
    }

    partial void OnRangeDaysChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomRange));
        if (_applyingState)
        {
            return;
        }

        // A new range replaces whatever day was opened.
        _backState = null;
        UpdateBack();
        Reload();
    }

    partial void OnSelectedAccountChanged(UsageAccountOption? value)
    {
        if (value is null)
        {
            RestoreSelection();
            return;
        }

        _selectedAccountId = value.AccountId;
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

        var restored = Accounts.FirstOrDefault(option => option.AccountId == _selectedAccountId) ?? Accounts[0];
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
        UpdateHeaders();
        if (_view is not null)
        {
            Chart = BuildChart(_view);
        }
    }

    partial void OnBreakdownIndexChanged(int value)
    {
        UpdateHeaders();
        UpdateSortGlyphs();
        if (_view is not null)
        {
            BreakdownRows = BuildBreakdown(_view);
        }
    }

    /// <summary>
    /// Titles follow the report on screen, not the range just picked, so they
    /// never describe data that has not arrived yet.
    /// </summary>
    private void UpdateHeaders()
    {
        var hourly = _view?.Primary.IsHourly == true;
        var period = hourly ? "Hourly" : "Daily";
        ChartTitle = MetricIndex == 1 ? $"{period} tokens" : $"{period} API value";
        TimeBreakdownLabel = hourly ? "HOUR" : "DAY";
        BreakdownColumnHeader = BreakdownIndex == 1 ? (hourly ? "Hour" : "Day") : "Model";
        ShowComparisonColumns = BreakdownIndex == 0 && _view?.Comparison is not null;
        BreakdownValueHeader = ShowComparisonColumns ? "API value A" : "API value";
        BreakdownTokensHeader = ShowComparisonColumns ? "Tokens A" : "Tokens";
        ChangeHeader = _view is { Comparison: not null } shown ? $"Change {shown.Direction.Label}" : "Change";
        BreakdownHint = BreakdownIndex == 1 && _view is { Primary.IsHourly: false } && BreakdownRows.Count > 0
            ? "Select a day to see it by hour"
            : string.Empty;
    }

    private void UpdateSortGlyphs()
    {
        var sort = BreakdownIndex == 1 ? _timeSort : EffectiveModelSort();
        var arrow = sort.Descending ? "▼" : "▲";
        LabelSortGlyph = sort.Column == UsageSortColumn.Label ? arrow : string.Empty;
        ValueSortGlyph = sort.Column == UsageSortColumn.Value ? arrow : string.Empty;
        ShareSortGlyph = sort.Column == UsageSortColumn.Share ? arrow : string.Empty;
        TokensSortGlyph = sort.Column == UsageSortColumn.Tokens ? arrow : string.Empty;
        ChangeSortGlyph = sort.Column == UsageSortColumn.Change ? arrow : string.Empty;
        PreviousValueSortGlyph = sort.Column == UsageSortColumn.PreviousValue ? arrow : string.Empty;
        PreviousTokensSortGlyph = sort.Column == UsageSortColumn.PreviousTokens ? arrow : string.Empty;
    }

    /// <summary>
    /// The model sort in force: a sort on a comparison column with no
    /// comparison on screen falls back to the default, since the column is
    /// hidden; so does a Share sort while the comparison hides Share.
    /// </summary>
    private UsageSortState EffectiveModelSort()
    {
        var comparing = _view?.Comparison is not null;
        var comparisonColumn = _modelSort.Column is UsageSortColumn.Change
            or UsageSortColumn.PreviousValue
            or UsageSortColumn.PreviousTokens;
        return (comparisonColumn && !comparing) || (_modelSort.Column == UsageSortColumn.Share && comparing)
            ? UsageSortState.ModelDefault
            : _modelSort;
    }

    private void UpdateBack()
    {
        CanGoBack = _backState is not null;
        BackLabel = _backState is { } state ? $"Back to {DescribeRange(state)}" : string.Empty;
    }

    private string DescribeRange(ViewState state) => state.RangeDays switch
    {
        PastDayRange => "past 24h",
        CustomRange => UsageNumberFormat.RangeLabel(state.CustomFrom, state.CustomTo, Today),
        var days => $"{days} days"
    };

    private ViewState CaptureState() => new(
        RangeDays,
        _customFrom,
        _customTo,
        IsComparing,
        _comparisonRange,
        BreakdownIndex,
        _modelSort,
        _timeSort);

    private UsagePeriod CurrentPeriod(DateOnly today) => RangeDays switch
    {
        PastDayRange => UsagePeriod.Past24Hours,
        CustomRange => UsagePeriod.Days(_customFrom, _customTo),
        var days => UsagePeriod.LastDays(Math.Max(1, days), today)
    };

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

        // One clock reading and one zone for the whole load, so the primary
        // and comparison periods agree on what today and midnight are.
        var now = DateTimeOffset.Now;
        var zone = _analytics.TimeZone;
        var today = UsagePeriods.Today(now, zone);
        var period = CurrentPeriod(today);
        var primary = UsagePeriods.Resolve(period, now, zone);
        var comparison = IsComparing
            ? UsagePeriods.ResolveComparison(period, _comparisonRange, now, zone)
            : null;
        var state = CaptureState();

        try
        {
            if (invalidate)
            {
                _analytics.Invalidate();
            }

            var accountList = await Task.Run(() => _analytics.GetAccountsAsync(token), token).ConfigureAwait(true);
            if (!IsCurrent(cts))
            {
                return;
            }

            // Read after the picker is applied: a caller that asked for one
            // account's view moves the selection here, and every report of this
            // load has to be the one the picker now claims to show.
            ApplyAccounts(accountList);
            var accountId = SelectedAccount?.AccountId;
            string[]? filter = accountId is null ? null : [accountId];

            var primaryTask = Task.Run(() => Fetch(primary, filter, token), token);
            var comparisonTask = comparison is null
                ? Task.FromResult<UsageReport?>(null)
                : Task.Run<UsageReport?>(async () => await Fetch(comparison, filter, token).ConfigureAwait(false), token);
            var earliestTask = comparison is null
                ? Task.FromResult<DateOnly?>(null)
                : Task.Run(() => _analytics.GetEarliestActivityDayAsync(filter, token), token);

            await Task.WhenAll(primaryTask, comparisonTask, earliestTask).ConfigureAwait(true);
            if (!IsCurrent(cts))
            {
                return;
            }

            // Both reports land in one step, so the window never shows a
            // current period beside a comparison from an older request.
            Apply(new LoadedView(
                period,
                primary,
                primaryTask.Result,
                comparison,
                comparisonTask.Result,
                earliestTask.Result,
                today,
                zone,
                state,
                now));
        }
        catch (OperationCanceledException)
        {
            // A newer request replaced this one; its result is the live one.
        }
        catch (Exception exception)
        {
            // A request that was already replaced must not wipe the newer
            // request's view, whichever order the two finish in.
            if (IsCurrent(cts))
            {
                Log.Warning(exception, "Usage report failed");
                _view = null;
                HasData = false;
                StatusText = "Could not read the local agent logs. See the log file for details.";
            }
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

    private bool IsCurrent(CancellationTokenSource cts) =>
        ReferenceEquals(_inFlight, cts) && !cts.IsCancellationRequested;

    private Task<UsageReport> Fetch(UsageResolvedPeriod period, string[]? filter, CancellationToken token) =>
        period.Window is { } window
            ? _analytics.GetWindowReportAsync(window, filter, token)
            : _analytics.GetReportAsync(period.Range, filter, token);

    private void ApplyAccounts(IReadOnlyList<UsageAccountInfo> discovered)
    {
        var options = new List<UsageAccountOption>(discovered.Count + 1) { AllAccountsOption };
        options.AddRange(discovered.Select(account => new UsageAccountOption(account.AccountId, account.DisplayName)));

        // A one-shot request from another window wins over whatever the picker
        // was left on; it is consumed here whether or not it matches an account.
        var requested = _pendingAccountId;
        _pendingAccountId = null;

        var sameList = Accounts.Count == options.Count &&
            Accounts.Zip(options).All(pair => pair.First == pair.Second);

        var wanted = requested is null
            ? SelectedAccount?.AccountId
            : options
                .FirstOrDefault(option => string.Equals(option.AccountId, requested, StringComparison.OrdinalIgnoreCase))
                ?.AccountId;

        if (sameList && wanted == SelectedAccount?.AccountId)
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
            SelectedAccount = live.FirstOrDefault(option => option.AccountId == wanted) ?? live[0];
        }
        finally
        {
            _suppressReload = false;
        }
    }

    private void Apply(LoadedView view)
    {
        _view = view;
        var report = view.Report;
        var comparison = view.Comparison;

        // While comparing, the two periods carry the same A and B names the
        // picker, the table headers and the hero use.
        RangeLabel = view.ComparisonPeriod is null
            ? Describe(view.Primary, view.Today, view.Zone)
            : $"A: {Describe(view.Primary, view.Today, view.Zone)}";
        ComparisonLabel = view.ComparisonPeriod is { } compared
            ? $"vs B: {Describe(compared, view.Today, view.Zone)}"
            : string.Empty;

        HasData = !report.IsEmpty || comparison is { IsEmpty: false };
        StatusText = HasData
            ? string.Empty
            : comparison is null
                ? "No local agent usage in this range."
                : "No local agent usage in either range.";

        var totals = report.Totals;
        TotalCostText = UsageNumberFormat.Money(totals.CostUsd);
        TotalDeltaText = comparison is null
            ? string.Empty
            : $"{view.Direction.Label} {UsageNumberFormat.Delta(view.Direction.Between(totals, comparison.Totals))}";
        TotalPreviousText = comparison is null
            ? string.Empty
            : $"B: {Cost(comparison.Totals)}";

        // Unpriced tokens are counted but not costed, so the hero figure is a
        // floor, not the truth. Say so instead of implying those models are free,
        // for the comparison period as much as for the current one.
        var unpriced = report.UnpricedModels
            .Concat(comparison?.UnpricedModels ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var hasUnpriced = totals.UnpricedTokens > 0 || comparison?.Totals.UnpricedTokens > 0;
        UnpricedNote = hasUnpriced && unpriced > 0
            ? $"excludes {unpriced} unpriced model{(unpriced == 1 ? string.Empty : "s")}"
            : string.Empty;

        ComparisonNote = BuildComparisonNote(view);
        ProviderRows = BuildProviderRows(report, comparison, view.Direction);
        Tiles = BuildTiles(report, comparison, view.Direction);
        Chart = BuildChart(view);
        BreakdownRows = BuildBreakdown(view);
        UpdateHeaders();
        UpdateSortGlyphs();
    }

    private static string BuildComparisonNote(LoadedView view)
    {
        if (view.ComparisonPeriod is not { } compared)
        {
            return string.Empty;
        }

        var notes = new List<string>(4);
        if (compared.DayCount != view.Primary.DayCount)
        {
            notes.Add($"The comparison is {Days(compared.DayCount)}, this range {Days(view.Primary.DayCount)}: changes compare the average per day.");
        }

        if (UsesAverageDay(view.Primary, view.State.ComparisonRange is not null, compared) && compared.DayCount > 1)
        {
            notes.Add("The dashed line is an average day of the comparison.");
        }

        // A period that includes today is partial against one that does not.
        // Rolling windows are always whole 24 hours, so they never count.
        var comparisonIsRolling = view.Period.IsRolling && view.State.ComparisonRange is null;
        var aIncludesToday = !view.Period.IsRolling && view.Primary.LastDay >= view.Today;
        var bIncludesToday = !comparisonIsRolling && compared.LastDay >= view.Today;
        if (aIncludesToday != bIncludesToday)
        {
            notes.Add(aIncludesToday ? "A includes today so far." : "B includes today so far.");
        }

        // Missing history matters for whichever period comes first, A or B.
        var earlierFirstDay = view.Primary.FirstDay < compared.FirstDay ? view.Primary.FirstDay : compared.FirstDay;
        if (view.EarliestActivity is { } earliest && earlierFirstDay < earliest)
        {
            notes.Add(
                $"The earlier period starts before the earliest recorded usage ({UsageNumberFormat.DayLabel(earliest)}); " +
                "older logs may have been deleted.");
        }

        return string.Join(" ", notes);
    }

    private static string Days(int count) => count == 1 ? "1 day" : $"{count} days";

    private static string Describe(UsageResolvedPeriod period, DateOnly today, TimeZoneInfo zone)
    {
        // Only the rolling 24 hours have clock-time bounds; every other
        // period is whole days, even when it is shown by hour.
        if (period.IsRolling && period.Window is { } window)
        {
            return UsageNumberFormat.RangeLabel(
                TimeZoneInfo.ConvertTime(window.Since, zone),
                TimeZoneInfo.ConvertTime(window.Until, zone));
        }

        return UsageNumberFormat.RangeLabel(period.FirstDay, period.LastDay, today);
    }

    private static IReadOnlyList<UsageProviderRow> BuildProviderRows(UsageReport report, UsageReport? comparison, UsageChangeDirection direction)
    {
        var totalCost = report.Totals.CostUsd;
        var totalTokens = report.Totals.Tokens.ProcessedTokens;
        var current = report.ByProvider.ToDictionary(provider => provider.Provider, provider => provider.Totals);
        var previous = comparison?.ByProvider.ToDictionary(provider => provider.Provider, provider => provider.Totals)
            ?? [];

        // A provider only the comparison period used still gets a row, so its
        // drop to zero is visible instead of silently missing.
        return current.Keys
            .Union(previous.Keys)
            .Select(provider => (
                Provider: provider,
                Totals: current.GetValueOrDefault(provider, UsageTotals.Empty),
                Previous: previous.GetValueOrDefault(provider, UsageTotals.Empty)))
            .OrderByDescending(entry => entry.Totals.CostUsd)
            .ThenByDescending(entry => entry.Totals.Tokens.ProcessedTokens)
            .ThenByDescending(entry => entry.Previous.CostUsd)
            .Select(entry =>
            {
                var tokens = entry.Totals.Tokens.ProcessedTokens;
                var share = totalCost > 0m
                    ? (double)(entry.Totals.CostUsd / totalCost)
                    : (totalTokens > 0 ? (double)tokens / totalTokens : 0d);

                var caption = totalCost > 0m
                    ? $"{UsageNumberFormat.Percent(entry.Totals.CostUsd, totalCost)} of estimate · {UsageNumberFormat.Tokens(tokens)} tokens"
                    : $"{UsageNumberFormat.Percent(tokens, totalTokens)} of tokens · {UsageNumberFormat.Tokens(tokens)} tokens";

                return new UsageProviderRow(
                    DisplayName(entry.Provider),
                    Key(entry.Provider),
                    Cost(entry.Totals),
                    share,
                    caption,
                    comparison is null
                        ? string.Empty
                        : WithPrevious(Cost(entry.Previous), UsageNumberFormat.Delta(direction.Between(entry.Totals, entry.Previous))));
            })
            .ToList();
    }

    private static IReadOnlyList<UsageStatTile> BuildTiles(UsageReport report, UsageReport? comparison, UsageChangeDirection direction)
    {
        var tokens = report.Totals.Tokens;
        var hourly = report.Window is not null;
        var activePeriods = Math.Max(1, hourly ? report.Hourly.Count : report.Daily.Count);
        var perActivePeriod = tokens.ProcessedTokens / activePeriods;

        string TokenDelta(Func<UsageTokens, long> pick) => comparison is null
            ? string.Empty
            : WithPrevious(
                UsageNumberFormat.Tokens(pick(comparison.Totals.Tokens)),
                UsageNumberFormat.Delta(direction.OfTokens(pick(tokens), pick(comparison.Totals.Tokens)), showBasis: false));

        // Savings are money, but money that leaves out unpriced models is a
        // floor on either side; cached-input tokens are the fair fallback.
        var savingsDelta = string.Empty;
        if (comparison is not null)
        {
            var fullyPriced = report.Totals.UnpricedTokens == 0 && comparison.Totals.UnpricedTokens == 0;
            savingsDelta = WithPrevious(
                UsageNumberFormat.Money(comparison.Totals.CacheSavingsUsd),
                UsageNumberFormat.Delta(fullyPriced
                    ? direction.OfCost(report.Totals.CacheSavingsUsd, comparison.Totals.CacheSavingsUsd)
                    : direction.OfTokens(tokens.CacheReadInputTokens, comparison.Totals.Tokens.CacheReadInputTokens)));
        }

        return
        [
            new UsageStatTile(
                "Processed tokens",
                UsageNumberFormat.Tokens(tokens.ProcessedTokens),
                $"{UsageNumberFormat.Tokens(perActivePeriod)} per active {(hourly ? "hour" : "day")}",
                TokenDelta(value => value.ProcessedTokens)),
            new UsageStatTile(
                "Cached input",
                UsageNumberFormat.Tokens(tokens.CacheReadInputTokens),
                $"{UsageNumberFormat.Percent(tokens.CacheReadInputTokens, tokens.InputTokens)} of observed input",
                TokenDelta(value => value.CacheReadInputTokens)),
            new UsageStatTile(
                "Uncached input",
                UsageNumberFormat.Tokens(tokens.UncachedInputTokens),
                $"{UsageNumberFormat.Tokens(tokens.CacheWriteInputTokens)} cache writes",
                TokenDelta(value => value.UncachedInputTokens)),
            new UsageStatTile(
                "Output",
                UsageNumberFormat.Tokens(tokens.OutputTokens),
                $"includes {UsageNumberFormat.Tokens(tokens.ReasoningOutputTokens)} reasoning",
                TokenDelta(value => value.OutputTokens)),
            new UsageStatTile(
                "Cache savings",
                UsageNumberFormat.Money(report.Totals.CacheSavingsUsd),
                $"{UsageNumberFormat.Multiplier(report.Totals.CacheSavingsUsd, report.Totals.CostUsd)} the API list-price estimate",
                savingsDelta)
        ];
    }

    /// <summary>"B: $258.60 · ▼ 75.6%": period B's own figure, then the change.</summary>
    private static string WithPrevious(string previous, string delta) =>
        delta.Length == 0 ? $"B: {previous}" : $"B: {previous} \u00B7 {delta}";

    private static IReadOnlyList<UsageStatTile> EmptyTiles() =>
    [
        new UsageStatTile("Processed tokens", "0", string.Empty),
        new UsageStatTile("Cached input", "0", string.Empty),
        new UsageStatTile("Uncached input", "0", string.Empty),
        new UsageStatTile("Output", "0", string.Empty),
        new UsageStatTile("Cache savings", "$0.00", string.Empty)
    ];

    private UsageChartData BuildChart(LoadedView view)
    {
        var primary = view.Primary;
        var count = primary.PointCount;
        if (count <= 0)
        {
            return UsageChartData.Empty;
        }

        var metric = MetricIndex == 1 ? UsageMetric.Tokens : UsageMetric.Cost;
        var report = view.Report;
        var comparison = view.Comparison;
        var providers = report.ByProvider
            .Select(entry => entry.Provider)
            .Union(comparison?.ByProvider.Select(entry => entry.Provider) ?? [])
            .Order()
            .ToList();

        // How B lines up under A. Hourly ranges of whole days match B by
        // clock: day N at 14:00 in A against day N at 14:00 in B, so a
        // daylight-saving change in either never shifts the rest by an hour.
        // The rolling 24 hours match the exact 24 hours before them; longer
        // picked days under an hourly range become an average day.
        var slots = primary.Window?.HourStarts();
        var averageDay = view.ComparisonPeriod is { } comparedPeriod &&
            UsesAverageDay(primary, view.State.ComparisonRange is not null, comparedPeriod);
        UsageClockSlot?[]? clock = view.ComparisonPeriod is { Window: not null } clockPeriod &&
            slots is not null && !primary.IsRolling && !averageDay
                ? UsageSeriesAlignment.ClockMap(slots, primary.FirstDay, clockPeriod.FirstDay, clockPeriod.DayCount, view.Zone)
                : null;

        // A point still in progress is marked, and the points after it are
        // future and left out, so a partial day never reads as a drop. An
        // average day has no "now" of its own.
        var partial = UsageSeriesAlignment.PartialIndex(primary, view.Now, view.Zone);
        // On a clock map, where B's future starts and which point stands for
        // B's hour in progress can differ (B's last hour, an hour A skips), so
        // the cut and the "so far" mark are worked out separately.
        var comparisonPartial = view.ComparisonPeriod is { } periodB && !averageDay
            ? clock is not null
                ? UsageSeriesAlignment.ClockPartialIndex(clock, view.Now, view.Zone)
                : UsageSeriesAlignment.PartialIndex(periodB, view.Now, view.Zone)
            : null;
        var comparisonInProgress = clock is not null
            ? UsageSeriesAlignment.ClockInProgressIndex(clock, view.Now, view.Zone)
            : comparisonPartial;

        var series = providers
            .Select(provider => new UsageChartSeries(
                provider,
                UsageSeriesAlignment.WithoutFuture(Align(report, primary, provider, count, metric), partial)))
            .ToList();

        var comparisonSeries = comparison is not null && view.ComparisonPeriod is { } compared
            ? providers
                .Select(provider => new UsageChartSeries(
                    provider,
                    UsageSeriesAlignment.WithoutFuture(
                        clock is not null
                            ? UsageSeriesAlignment.ByClock(comparison.HourlyByModel, provider, clock, view.Zone, metric)
                            : AlignComparison(view, comparison, compared, provider, count, metric),
                        comparisonPartial)))
                .ToList()
            : [];

        IReadOnlyList<string> labels = slots is not null
            ? slots.Select(hour => HourAxisLabel(primary, hour, view.Zone)).ToList()
            : Enumerable.Range(0, count)
                .Select(index => UsageNumberFormat.AxisDayLabel(primary.FirstDay.AddDays(index)))
                .ToList();

        var useTokens = metric == UsageMetric.Tokens;
        return new UsageChartData
        {
            Labels = labels,
            Series = series,
            ComparisonSeries = comparisonSeries,
            ComparisonLabels = clock is not null
                ? ClockAxisLabels(clock, primary)
                : ComparisonAxisLabels(view, count, averageDay),
            PartialIndex = partial,
            ComparisonPartialIndex = comparisonInProgress,
            AxisLabel = useTokens
                ? value => UsageNumberFormat.AxisTokens((long)Math.Round(value))
                : value => UsageNumberFormat.AxisCost((decimal)value)
        };
    }

    /// <summary>B's own day and hour under every point of a clock-matched hourly chart.</summary>
    private static IReadOnlyList<string> ClockAxisLabels(UsageClockSlot?[] clock, UsageResolvedPeriod primary) =>
        clock
            .Select(slot => slot is { } match
                ? primary.DayCount <= 1
                    ? UsageNumberFormat.AxisHourLabel(new DateTimeOffset(match.Day.ToDateTime(new TimeOnly(match.Hour, 0)), TimeSpan.Zero))
                    : UsageNumberFormat.AxisDayHourLabel(new DateTimeOffset(match.Day.ToDateTime(new TimeOnly(match.Hour, 0)), TimeSpan.Zero))
                : string.Empty)
            .ToList();

    /// <summary>
    /// The comparison's own date or hour under every point, empty where it has
    /// none, so the dashed line is read against its real dates. An average day
    /// has no dates of its own.
    /// </summary>
    private static IReadOnlyList<string> ComparisonAxisLabels(LoadedView view, int count, bool averageDay)
    {
        if (view.ComparisonPeriod is not { } compared || averageDay)
        {
            return [];
        }

        if (compared.Window is { } window)
        {
            var hours = window.HourStarts();
            return Enumerable.Range(0, count)
                .Select(index => index < hours.Count
                    ? HourAxisLabel(compared, hours[index], view.Zone)
                    : string.Empty)
                .ToList();
        }

        return Enumerable.Range(0, count)
            .Select(index => index < compared.DayCount
                ? UsageNumberFormat.AxisDayLabel(compared.FirstDay.AddDays(index))
                : string.Empty)
            .ToList();
    }

    /// <summary>
    /// An hour on the axis: just the time within one day or the rolling 24
    /// hours, with the date when the range spans several days.
    /// </summary>
    private static string HourAxisLabel(UsageResolvedPeriod period, DateTimeOffset hour, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(hour, zone);
        return period.IsRolling || period.DayCount <= 1
            ? UsageNumberFormat.AxisHourLabel(local)
            : UsageNumberFormat.AxisDayHourLabel(local);
    }

    /// <summary>
    /// True when picked days are laid under an hourly range as an average day
    /// matched by clock hour: always under the rolling 24 hours, whose clock
    /// times do not start at midnight, and otherwise only when the picked days
    /// are too long to be shown hour by hour themselves. Short picked days
    /// under whole days line up day for day and hour for hour instead.
    /// </summary>
    private static bool UsesAverageDay(UsageResolvedPeriod primary, bool picked, UsageResolvedPeriod compared) =>
        picked && primary.IsHourly && (primary.IsRolling || compared.DayCount > UsagePeriods.MaxHourlyDays);

    /// <summary>
    /// The comparison's dashed line. The previous period lines up point for
    /// point. Picked days line up from their first day when both sides are
    /// daily, with a gap past the shorter one; under an hourly range they
    /// become an average day, matched by clock hour.
    /// </summary>
    private static double[] AlignComparison(
        LoadedView view,
        UsageReport comparison,
        UsageResolvedPeriod compared,
        UsageProviderKind provider,
        int count,
        UsageMetric metric)
    {
        if (view.State.ComparisonRange is null)
        {
            return Align(comparison, compared, provider, count, metric);
        }

        if (view.Primary.Window is { } window)
        {
            return UsesAverageDay(view.Primary, picked: true, compared)
                ? UsageSeriesAlignment.AverageDay(comparison.HourlyByModel, provider, window.HourStarts(), compared.DayCount, view.Zone, metric)
                : UsageSeriesAlignment.Hourly(comparison.HourlyByModel, provider, compared.Window!.Value, count, metric);
        }

        return UsageSeriesAlignment.Daily(comparison.DailyByModel, provider, compared.FirstDay, count, metric, compared.DayCount);
    }

    private static double[] Align(
        UsageReport report,
        UsageResolvedPeriod period,
        UsageProviderKind provider,
        int count,
        UsageMetric metric) =>
        period.Window is { } window
            ? UsageSeriesAlignment.Hourly(report.HourlyByModel, provider, window, count, metric)
            : UsageSeriesAlignment.Daily(report.DailyByModel, provider, period.FirstDay, count, metric);

    private IReadOnlyList<UsageBreakdownRow> BuildBreakdown(LoadedView view)
    {
        var report = view.Report;
        var totalCost = report.Totals.CostUsd;
        var byTime = BreakdownIndex == 1;

        var items = byTime
            ? view.Primary.IsHourly
                ? UsageBreakdown.Hours(report, view.Zone, view.Today)
                : UsageBreakdown.Days(report, view.Today)
            : UsageBreakdown.Models(report, view.Comparison, view.Direction);

        var canDrill = byTime && !view.Primary.IsHourly;
        var sort = byTime ? _timeSort : EffectiveModelSort();

        return UsageBreakdown.Sort(items, sort)
            .Select(item => new UsageBreakdownRow(
                item.Label,
                item.Provider is { } provider ? Key(provider) : string.Empty,
                item.IsUnpriced ? "unpriced" : UsageNumberFormat.Money(item.Totals.CostUsd),
                UsageNumberFormat.Percent(item.Totals.CostUsd, totalCost),
                UsageNumberFormat.Tokens(item.Totals.Tokens.ProcessedTokens),
                byTime ? string.Empty : UsageNumberFormat.Delta(item.Delta),
                canDrill ? item.Day : null,
                item.PreviousTotals is { } previous
                    ? item.IsPreviousUnpriced ? "unpriced" : UsageNumberFormat.Money(previous.CostUsd)
                    : string.Empty,
                item.PreviousTotals is { } previousTokens
                    ? UsageNumberFormat.Tokens(previousTokens.Tokens.ProcessedTokens)
                    : string.Empty))
            .ToList();
    }

    /// <summary>
    /// A bucket's cost, or "unpriced" when it consumed tokens no price covers.
    /// A bucket that mixes priced and unpriced models still shows its priced
    /// cost: the hero footnote is what warns that the figure is a floor.
    /// </summary>
    private static string Cost(UsageTotals totals) =>
        UsageNumberFormat.CostOrUnpriced(totals.CostUsd, totals.UnpricedTokens);

    private static string DisplayName(UsageProviderKind provider) =>
        provider == UsageProviderKind.Claude ? "Claude Code" : "Codex";

    private static string Key(UsageProviderKind provider) =>
        provider == UsageProviderKind.Claude ? "claude" : "codex";

    /// <summary>Everything one completed load produced, applied in one step.</summary>
    private sealed record LoadedView(
        UsagePeriod Period,
        UsageResolvedPeriod Primary,
        UsageReport Report,
        UsageResolvedPeriod? ComparisonPeriod,
        UsageReport? Comparison,
        DateOnly? EarliestActivity,
        DateOnly Today,
        TimeZoneInfo Zone,
        ViewState State,
        DateTimeOffset Now)
    {
        /// <summary>
        /// Which way changes run (from the earlier period to the later one)
        /// and how different lengths are scaled to an average per day.
        /// </summary>
        public UsageChangeDirection Direction => ComparisonPeriod is { } compared
            ? UsageChangeDirection.For(Primary, compared, Zone)
            : UsageChangeDirection.Default;
    }

    /// <summary>What Back restores after a day was opened.</summary>
    private sealed record ViewState(
        int RangeDays,
        DateOnly CustomFrom,
        DateOnly CustomTo,
        bool IsComparing,
        UsagePeriod? ComparisonRange,
        int BreakdownIndex,
        UsageSortState ModelSort,
        UsageSortState TimeSort);
}
