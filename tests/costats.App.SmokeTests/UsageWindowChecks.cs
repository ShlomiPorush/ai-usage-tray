#nullable enable

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using costats.App;
using costats.App.ViewModels;
using costats.Core.Analytics;
using costats.Infrastructure.Analytics;

/// <summary>
/// An analytics service whose reports complete only when the test says so,
/// so loads can be made to finish in any order.
/// </summary>
sealed class ScriptedAnalytics : IUsageAnalyticsService
{
    public readonly List<(UsageDateRange Range, UsageTimeWindow? Window, TaskCompletionSource<UsageReport> Result)> Requests = [];

    public TimeZoneInfo TimeZone => TimeZoneInfo.Local;

    public Task<UsageReport> GetReportAsync(UsageDateRange range, IReadOnlyCollection<string>? accountIds = null, CancellationToken cancellationToken = default) =>
        Next(range, null);

    public Task<UsageReport> GetWindowReportAsync(UsageTimeWindow window, IReadOnlyCollection<string>? accountIds = null, CancellationToken cancellationToken = default) =>
        Next(UsageDateRange.All, window);

    public Task<IReadOnlyList<UsageAccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UsageAccountInfo>>([]);

    public DateOnly? EarliestActivity { get; set; }

    public Task<DateOnly?> GetEarliestActivityDayAsync(IReadOnlyCollection<string>? accountIds = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(EarliestActivity);

    public UsageCacheInfo GetCacheInfo() => new(0, 0);

    public Task<UsageCacheInfo> ClearCacheAsync(CancellationToken cancellationToken = default) => Task.FromResult(GetCacheInfo());

    public void Invalidate()
    {
    }

    public int Count
    {
        get
        {
            lock (Requests)
            {
                return Requests.Count;
            }
        }
    }

    public (UsageDateRange Range, UsageTimeWindow? Window, TaskCompletionSource<UsageReport> Result) this[int index]
    {
        get
        {
            lock (Requests)
            {
                return Requests[index];
            }
        }
    }

    private Task<UsageReport> Next(UsageDateRange range, UsageTimeWindow? window)
    {
        var result = new TaskCompletionSource<UsageReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Requests)
        {
            Requests.Add((range, window, result));
        }

        return result.Task;
    }
}

/// <summary>
/// An analytics service over generated samples, run through the real
/// aggregator, so the window renders realistic numbers without user logs.
/// </summary>
sealed class SyntheticAnalytics : IUsageAnalyticsService
{
    private readonly List<UsageSample> _samples = [];

    public SyntheticAnalytics(DateTimeOffset now)
    {
        var random = new Random(7);
        for (var hour = 0; hour < 24 * 75; hour++)
        {
            var at = now.AddHours(-hour);
            var weight = 1d + Math.Sin(hour / 20d) + (hour < 24 * 30 ? 0.6 : 0);
            if (random.NextDouble() > 0.55)
            {
                continue;
            }

            _samples.Add(Sample(at, UsageProviderKind.Claude, "claude-opus-5-5", (long)(40_000 * weight * random.NextDouble())));
            if (hour % 3 == 0)
            {
                _samples.Add(Sample(at, UsageProviderKind.Claude, "claude-sonnet-5-5", (long)(30_000 * weight)));
            }

            if (hour % 2 == 0)
            {
                _samples.Add(Sample(at, UsageProviderKind.Codex, "gpt-5-codex", (long)(25_000 * weight * random.NextDouble())));
            }

            // A model only the older weeks used, so the comparison has a drop.
            if (hour > 24 * 32 && hour % 5 == 0)
            {
                _samples.Add(Sample(at, UsageProviderKind.Codex, "gpt-5-mini", 50_000));
            }
        }
    }

    public TimeZoneInfo TimeZone => TimeZoneInfo.Local;

    public Task<UsageReport> GetReportAsync(UsageDateRange range, IReadOnlyCollection<string>? accountIds = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(UsageAggregator.Aggregate(_samples, new UsageAggregationOptions { Range = range, TimeZone = TimeZone }));

    public Task<UsageReport> GetWindowReportAsync(UsageTimeWindow window, IReadOnlyCollection<string>? accountIds = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(UsageAggregator.Aggregate(_samples, new UsageAggregationOptions { Window = window, TimeZone = TimeZone }));

    public Task<IReadOnlyList<UsageAccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UsageAccountInfo>>([new UsageAccountInfo("claude-1", "Claude", UsageProviderKind.Claude)]);

    public Task<DateOnly?> GetEarliestActivityDayAsync(IReadOnlyCollection<string>? accountIds = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<DateOnly?>(DateOnly.FromDateTime(_samples.Min(sample => sample.Timestamp).LocalDateTime));

    public UsageCacheInfo GetCacheInfo() => new(0, 0);

    public Task<UsageCacheInfo> ClearCacheAsync(CancellationToken cancellationToken = default) => Task.FromResult(GetCacheInfo());

    public void Invalidate()
    {
    }

    private static UsageSample Sample(DateTimeOffset at, UsageProviderKind provider, string model, long tokens) =>
        new(at.ToUniversalTime(), provider, provider == UsageProviderKind.Claude ? "claude-1" : "codex", model,
            new UsageTokens { UncachedInputTokens = tokens / 10, CacheReadInputTokens = tokens * 6, OutputTokens = tokens / 4 });
}

static class UsageWindowChecks
{
    /// <summary>Runs every Usage window check; throws on the first failure.</summary>
    public static void Run(Func<string, string> outputPath)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        StaleFailureDoesNotWipeNewerView();
        StaleSuccessDoesNotReplaceNewerView();
        EmptyCurrentPeriodStillShowsComparison();
        DrillDuringLoadUsesTheReportOnScreen();
        PickedComparisonOfAnotherLength();
        ComparePopupPicksTwoPeriods();
        EarlierPeriodAMeasuresTheChangeForward();
        ChartLabelsKeepTheirEndsAndNeverOverlap();
        RenderWindow(outputPath);
        Console.WriteLine("PASS: usage load races, empty current period with comparison, usage window and popups rendered.");
    }

    private static void StaleFailureDoesNotWipeNewerView()
    {
        var analytics = new ScriptedAnalytics();
        var viewModel = new UsageWindowViewModel(analytics);
        var first = viewModel.InitializeAsync();
        PumpUntil(() => analytics.Count == 1, "first request");

        viewModel.RangeDays = 7;
        PumpUntil(() => analytics.Count == 2, "second request");
        analytics[1].Result.SetResult(Report(7m));
        PumpUntil(() => !viewModel.IsLoading, "second load");
        Check(viewModel.TotalCostText == "$7.00", $"Newer load must apply, got {viewModel.TotalCostText}");

        analytics[0].Result.SetException(new IOException("stale failure"));
        PumpUntil(() => first.IsCompleted, "first load to finish");
        Check(viewModel.TotalCostText == "$7.00" && viewModel.HasData && viewModel.StatusText.Length == 0,
            "A stale load that fails after a newer one succeeded must not wipe the newer view");
    }

    private static void StaleSuccessDoesNotReplaceNewerView()
    {
        var analytics = new ScriptedAnalytics();
        var viewModel = new UsageWindowViewModel(analytics);
        var first = viewModel.InitializeAsync();
        PumpUntil(() => analytics.Count == 1, "first request");

        viewModel.RangeDays = 90;
        PumpUntil(() => analytics.Count == 2, "second request");
        analytics[1].Result.SetResult(Report(90m));
        PumpUntil(() => !viewModel.IsLoading, "second load");
        analytics[0].Result.SetResult(Report(30m));
        PumpUntil(() => first.IsCompleted, "first load to finish");
        Check(viewModel.TotalCostText == "$90.00", $"A stale success must not replace the newer view, got {viewModel.TotalCostText}");
    }

    private static void EmptyCurrentPeriodStillShowsComparison()
    {
        var analytics = new ScriptedAnalytics();
        var viewModel = new UsageWindowViewModel(analytics);
        viewModel.ApplyComparison(enabled: true);
        PumpUntil(() => analytics.Count == 2, "primary and comparison requests");

        var today = DateOnly.FromDateTime(DateTime.Now);
        for (var index = 0; index < 2; index++)
        {
            var request = analytics[index];
            var isCurrent = request.Range.To == today;
            request.Result.SetResult(isCurrent ? UsageReport.Empty : Report(12m, "gpt-5-mini"));
        }

        PumpUntil(() => !viewModel.IsLoading, "comparison load");
        Check(viewModel.HasData, "A comparison with usage must stay visible when the current period is empty");
        Check(viewModel.TotalDeltaText == "B → A ▼ 100%", $"Hero delta for a drop to zero, got '{viewModel.TotalDeltaText}'");
        Check(viewModel.TotalPreviousText == "B: $12.00", $"Hero must show period B's total, got '{viewModel.TotalPreviousText}'");
        Check(viewModel.ShowComparisonColumns && viewModel.BreakdownRows.Any(row =>
                row.Label == "gpt-5-mini" && row.ChangeText == "▼ 100%" &&
                row.ValueText == "$0.00" && row.PreviousValueText == "$12.00"),
            "A model only the comparison used must stay in the table with both periods' values");
    }

    private static void DrillDuringLoadUsesTheReportOnScreen()
    {
        var analytics = new ScriptedAnalytics();
        var viewModel = new UsageWindowViewModel(analytics) { BreakdownIndex = 1 };
        viewModel.ApplyComparison(enabled: true);
        PumpUntil(() => analytics.Count == 2, "30-day requests");

        var today = DateOnly.FromDateTime(DateTime.Now);
        var day = today.AddDays(-2);
        for (var index = 0; index < 2; index++)
        {
            var request = analytics[index];
            request.Result.SetResult(request.Range.To == today ? Report(5m, day: day) : Report(3m));
        }

        PumpUntil(() => !viewModel.IsLoading && viewModel.BreakdownRows.Any(row => row.CanDrill), "30-day view");
        var row = viewModel.BreakdownRows.First(candidate => candidate.DrillDay == day);

        // A 90-day load starts and is still running when the 30-day row is clicked.
        viewModel.RangeDays = 90;
        PumpUntil(() => analytics.Count == 4, "90-day requests");
        viewModel.OpenRowCommand.Execute(row);
        PumpUntil(() => analytics.Count == 6, "drilled day requests");

        var zone = TimeZoneInfo.Local;
        var expected = costats.Core.Analytics.UsagePeriods.DayWindow(day.AddDays(-30), zone);
        var windows = Enumerable.Range(4, 2).Select(index => analytics[index].Window).ToList();
        Check(windows.Contains(expected),
            "A day opened from the 30-day view must compare with the matching day 30 days earlier");
        Check(viewModel.BackLabel == "Back to 30 days", $"Back must return to the view the row came from, got '{viewModel.BackLabel}'");
    }

    private static void PickedComparisonOfAnotherLength()
    {
        var analytics = new ScriptedAnalytics();
        var viewModel = new UsageWindowViewModel(analytics) { RangeDays = 7 };
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-60);
        var to = from.AddDays(13);

        Check(!viewModel.ApplyComparison(enabled: true, today, today.AddDays(1)), "A comparison may not end in the future");
        Check(viewModel.ApplyComparison(enabled: true, to, from), "Picked days in either order must be accepted");
        PumpUntil(() => analytics.Count == 2, "primary and picked comparison requests");

        for (var index = 0; index < 2; index++)
        {
            var request = analytics[index];
            var isPicked = request.Range.From == from && request.Range.To == to;
            // Same daily spend on both sides: $7 over 7 days against $14 over 14.
            request.Result.SetResult(isPicked ? Report(14m) : Report(7m));
        }

        PumpUntil(() => !viewModel.IsLoading && viewModel.HasData, "picked comparison load");
        Check(viewModel.ComparisonRange == (from, to), "The picked days must be the comparison");
        Check(viewModel.TotalDeltaText.EndsWith(" 0.0%", StringComparison.Ordinal), $"Equal spend per day must show no change, got '{viewModel.TotalDeltaText}'");
        Check(viewModel.ComparisonNote.Contains("average per day", StringComparison.Ordinal),
            "A comparison of another length must say changes compare the average per day");
    }

    private static void ChartLabelsKeepTheirEndsAndNeverOverlap()
    {
        static double Measure(string label) => label.Length * 7d;

        // A 30-day axis with a 10-day B: B must still label its last day.
        var shortB = Enumerable.Range(0, 30).Select(index => index < 10 ? $"OCT {index + 1}" : string.Empty).ToList();
        var placed = costats.App.Controls.UsageChartLayout.PlaceLabels(shortB, Measure, 0d, 600d);
        Check(placed.Any(entry => entry.Index == 9), "A shorter B must keep the label of its last day");
        Check(placed.Any(entry => entry.Index == 0), "B must keep the label of its first day");

        // A narrow 30-day axis with a 17-day B: the end sits by the middle and
        // must not be drawn over it.
        foreach (var width in new[] { 440d, 300d, 200d, 120d })
        {
            var b17 = Enumerable.Range(0, 30).Select(index => index < 17 ? $"OCT {index + 1}" : string.Empty).ToList();
            var row = costats.App.Controls.UsageChartLayout.PlaceLabels(b17, Measure, 0d, width);
            for (var index = 1; index < row.Count; index++)
            {
                var previous = row[index - 1];
                Check(row[index].X >= previous.X + Measure(b17[previous.Index]),
                    $"Labels overlap at width {width}: {b17[previous.Index]} and {b17[row[index].Index]}");
            }
        }

        // "so far" clears every ring at the point: above the topmost when it
        // fits, otherwise below the lowest, never between them.
        var radius = costats.App.Controls.UsageChartLayout.RingRadius;
        var topRings = new[] { 10d, 13d };
        var below = costats.App.Controls.UsageChartLayout.SoFarTop(topRings, 14d, 10d);
        Check(below >= 13d + radius, $"No room above: the text must sit below the lowest ring, got {below}");
        var midRings = new[] { 100d, 104d };
        var above = costats.App.Controls.UsageChartLayout.SoFarTop(midRings, 14d, 10d);
        Check(above + 14d <= 100d - radius, $"Room above: the text must sit above the topmost ring, got {above}");

        // A plot narrower than the text must not throw; the text starts at the left edge.
        Check(costats.App.Controls.UsageChartLayout.ClampedLeft(50d, 40d, 20d, 45d) == 20d, "A text wider than the plot starts at its left edge");

        // A single point keeps its label at the left edge, as before.
        var single = costats.App.Controls.UsageChartLayout.PlaceLabels(["OCT 26"], Measure, 0d, 600d);
        Check(single.Count == 1 && single[0].X == 0d, "A single point's label stays at the left edge");
    }

    private static void EarlierPeriodAMeasuresTheChangeForward()
    {
        // The owner's case: A is the earlier period with $413.47, B the later
        // one with $869.90. That is a rise from A to B, never a drop.
        var today = DateOnly.FromDateTime(DateTime.Now);
        var aFrom = today.AddDays(-59);
        var aTo = aFrom.AddDays(29);
        var bFrom = aTo.AddDays(1);
        var bTo = bFrom.AddDays(29);
        var analytics = new ScriptedAnalytics { EarliestActivity = aFrom.AddDays(5) };
        var viewModel = new UsageWindowViewModel(analytics) { BreakdownIndex = 0 };
        Check(viewModel.ApplyComparisonPeriods((aFrom, aTo), (bFrom, bTo)), "Both periods must be accepted");
        PumpUntil(() => analytics.Count == 2, "both requests");

        for (var index = 0; index < 2; index++)
        {
            var request = analytics[index];
            request.Result.SetResult(request.Range.From == aFrom ? Report(413.47m) : Report(869.90m));
        }

        PumpUntil(() => !viewModel.IsLoading && viewModel.HasData, "A and B view");
        Check(viewModel.TotalDeltaText.StartsWith("A \u2192 B \u25B2", StringComparison.Ordinal),
            $"An earlier A with less spend must show a rise from A to B, got '{viewModel.TotalDeltaText}'");
        Check(viewModel.ChangeHeader == "Change A \u2192 B", $"The change column must name its direction, got '{viewModel.ChangeHeader}'");
        Check(viewModel.BreakdownRows.All(row => row.ChangeText.StartsWith("\u25B2", StringComparison.Ordinal)),
            "Every row that grew from A to B must show a rise");

        // B ends today and A starts before the earliest log: both caveats must
        // show even though it is B, not A, that holds today.
        Check(bTo == today, "The later period must end today for this check");
        Check(viewModel.ComparisonNote.Contains("B includes today so far", StringComparison.Ordinal),
            $"A partial later period B must be called out, got '{viewModel.ComparisonNote}'");
        Check(viewModel.ComparisonNote.Contains("earliest recorded usage", StringComparison.Ordinal),
            $"An earlier period A before the first log must be called out, got '{viewModel.ComparisonNote}'");
    }

    private static void ComparePopupPicksTwoPeriods()
    {
        System.Windows.Application.Current.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri("/AIUsageTray;component/Themes/Controls.xaml", UriKind.Relative) });

        var analytics = new ScriptedAnalytics();
        var viewModel = new UsageWindowViewModel(analytics);
        var window = new UsageWindow(viewModel) { Left = -20000, Top = -20000, ShowActivated = false };
        window.Show();
        _ = viewModel.InitializeAsync();
        PumpUntil(() => analytics.Count == 1, "30-day request");
        analytics[0].Result.SetResult(Report(30m));
        PumpUntil(() => !viewModel.IsLoading && viewModel.HasData, "30-day view");

        var button = (Button)window.FindName("CompareButton");
        var popup = (Popup)window.FindName("ComparePopup");
        var picker = (costats.App.Controls.DateRangePicker)window.FindName("ComparePicker");
        var periodA = (RadioButton)window.FindName("PeriodAOption");
        var periodB = (RadioButton)window.FindName("PeriodBOption");

        // Closing without Apply changes nothing.
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        Check(popup.IsOpen && periodA.IsChecked == true, "The picker opens on period A");
        popup.IsOpen = false;
        Pump();
        Check(!viewModel.IsComparing && analytics.Count == 1, "Closing the popup must not change or load anything");

        var today = DateOnly.FromDateTime(DateTime.Now);
        var aFrom = new DateOnly(today.Year, today.Month, 1).AddMonths(-2);
        var aTo = aFrom.AddDays(9);
        var bFrom = aFrom.AddMonths(-1);
        var bTo = aFrom.AddDays(-1);
        var apply = (Button)window.FindName("CompareApplyButton");

        // Half a selection is never applied: one click in B, then a move to A,
        // leaves B as it was and keeps Apply on the complete periods.
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        var periodBText = (TextBlock)window.FindName("PeriodBText");
        var originalB = periodBText.Text;
        periodB.IsChecked = true;
        Pump();
        ClickDay(picker, bFrom);
        Check(!apply.IsEnabled && periodBText.Text.Contains("pick the last day", StringComparison.Ordinal),
            "A period waiting for its last day must block Apply and say so");
        periodA.IsChecked = true;
        Pump();
        Check(periodBText.Text == originalB && apply.IsEnabled, "Leaving a half-picked period must drop the half selection");
        popup.IsOpen = false;
        Pump();
        Check(!viewModel.IsComparing && analytics.Count == 1, "Closing after a half selection must not change anything");

        // Four clicks: A is Aug 1 to Aug 10, then the calendar moves to B by
        // itself, and B is Jul 1 to Jul 31.
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        ClickDay(picker, aFrom);
        ClickDay(picker, aTo);
        Check(periodB.IsChecked == true, "After period A is complete the calendar must move on to period B");
        ClickDay(picker, bFrom);
        ClickDay(picker, bTo);

        apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, apply));
        PumpUntil(() => analytics.Count == 3, "both periods requested");
        Check(viewModel.IsCustomRange && viewModel.CustomFrom == aFrom && viewModel.CustomTo == aTo,
            "Period A must become the range on screen");
        Check(viewModel.ComparisonRange == (bFrom, bTo), "Period B must become the comparison");
        window.ForceClose();
    }

    /// <summary>Clicks a day in the picker, moving to its month first like a user would.</summary>
    private static void ClickDay(costats.App.Controls.DateRangePicker picker, DateOnly day)
    {
        for (var guard = 0; guard < 36; guard++)
        {
            Pump();
            var grid = (ItemsControl)picker.FindName("DayGrid");
            var cell = grid.Items.Cast<costats.App.Controls.DateRangePickerDay>()
                .FirstOrDefault(candidate => candidate.Date == day && !candidate.IsOutsideMonth);
            if (cell is not null)
            {
                var container = (FrameworkElement)grid.ItemContainerGenerator.ContainerFromItem(cell);
                var dayButton = FindChild<Button>(container);
                dayButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, dayButton));
                Pump();
                return;
            }

            var shownMonth = grid.Items.Cast<costats.App.Controls.DateRangePickerDay>().First(candidate => !candidate.IsOutsideMonth).Date;
            var navigate = (Button)picker.FindName(day < shownMonth ? "PreviousMonthButton" : "NextMonthButton");
            navigate.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, navigate));
        }

        throw new Exception($"Could not reach {day} in the picker");
    }

    private static T FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindChildOrNull<T>(VisualTreeHelper.GetChild(root, index)) is { } found)
            {
                return found;
            }
        }

        throw new Exception($"No {typeof(T).Name} under {root}");
    }

    private static T? FindChildOrNull<T>(DependencyObject root) where T : DependencyObject
    {
        try
        {
            return FindChild<T>(root);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void RenderWindow(Func<string, string> outputPath)
    {
        var resources = System.Windows.Application.Current.Resources.MergedDictionaries;
        resources.Add(new ResourceDictionary { Source = new Uri("/AIUsageTray;component/Themes/Controls.xaml", UriKind.Relative) });

        foreach (var theme in new[] { "Dark", "Light" })
        {
            var themeDictionary = new ResourceDictionary
            {
                Source = new Uri($"/AIUsageTray;component/Themes/Theme{theme}.xaml", UriKind.Relative)
            };
            // Last wins: the theme must override the dark one the fixture starts with.
            resources.Add(themeDictionary);

            var viewModel = new UsageWindowViewModel(new SyntheticAnalytics(DateTimeOffset.Now));
            var window = new UsageWindow(viewModel)
            {
                Width = 1180,
                Height = 900,
                Left = -20000,
                Top = -20000,
                ShowActivated = false
            };
            window.Show();
            viewModel.ApplyComparison(enabled: true);
            PumpUntil(() => viewModel.HasData && !viewModel.IsLoading, "synthetic report");
            Capture(window, outputPath($"usage-compare-{theme.ToLowerInvariant()}.png"));
            window.Width = 900;
            Pump();
            Capture(window, outputPath($"usage-compare-900-{theme.ToLowerInvariant()}.png"));
            window.Width = 1180;
            Pump();

            viewModel.SortCommand.Execute("Tokens");
            viewModel.SortCommand.Execute("Tokens");
            Pump();
            Check(viewModel.TokensSortGlyph == "▲", "Second click on a header must reverse the sort");

            viewModel.BreakdownIndex = 1;
            Pump();
            var day = viewModel.BreakdownRows.First(row => row.CanDrill);
            viewModel.OpenRowCommand.Execute(day);
            PumpUntil(() => viewModel.CanGoBack && !viewModel.IsLoading && viewModel.TimeBreakdownLabel == "HOUR", "drill into a day");
            Check(viewModel.IsCustomRange && viewModel.CustomFrom == day.DrillDay, "Opening a day row must show that day");
            Capture(window, outputPath($"usage-day-{theme.ToLowerInvariant()}.png"));

            viewModel.BackCommand.Execute(null);
            PumpUntil(() => !viewModel.CanGoBack && !viewModel.IsLoading && viewModel.RangeDays == 30, "back");
            Check(viewModel.BreakdownIndex == 1 && viewModel.TokensSortGlyph.Length == 0,
                "Back must restore the range, breakdown mode and its sort");

            OpenPopup(window, "CustomRangeButton", "RangePopup", outputPath($"usage-range-popup-{theme.ToLowerInvariant()}.png"));
            OpenPopup(window, "CompareButton", "ComparePopup", outputPath($"usage-compare-popup-{theme.ToLowerInvariant()}.png"));
            var periodB = (RadioButton)window.FindName("PeriodBOption");
            OpenPopup(window, "CompareButton", "ComparePopup", outputPath($"usage-compare-pick-b-{theme.ToLowerInvariant()}.png"), () => periodB.IsChecked = true);

            // A picked comparison of another length: 10 days against 30.
            var today = DateOnly.FromDateTime(DateTime.Now);
            viewModel.ApplyComparison(enabled: true, today.AddDays(-60), today.AddDays(-51));
            PumpUntil(() => !viewModel.IsLoading && viewModel.ComparisonLabel.Length > 0 && viewModel.ComparisonNote.Contains("average per day", StringComparison.Ordinal), "picked comparison");
            Capture(window, outputPath($"usage-compare-picked-{theme.ToLowerInvariant()}.png"));
            OpenPopup(window, "CompareButton", "ComparePopup", outputPath($"usage-compare-pick-{theme.ToLowerInvariant()}.png"));

            window.ForceClose();
            resources.Remove(themeDictionary);
        }
    }

    private static void OpenPopup(Window window, string buttonName, string popupName, string path, Action? prepare = null)
    {
        var button = (Button)window.FindName(buttonName);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        prepare?.Invoke();
        var popup = (Popup)window.FindName(popupName);
        Pump();
        Check(popup.IsOpen, $"{popupName} must open");
        Capture((FrameworkElement)popup.Child, path);
        popup.IsOpen = false;
        Pump();
    }

    private static UsageReport Report(decimal cost, string model = "claude-opus-5-5", DateOnly? day = null)
    {
        var totals = new UsageTotals
        {
            CostUsd = cost,
            RequestCount = 1,
            Tokens = new UsageTokens { UncachedInputTokens = 1_000 }
        };

        var provider = model.StartsWith("gpt", StringComparison.Ordinal) ? UsageProviderKind.Codex : UsageProviderKind.Claude;
        return new UsageReport
        {
            Totals = totals,
            ByModel = [new ModelUsage(model, provider, true, totals)],
            Daily = day is { } date ? [new DailyUsage(date, totals)] : [],
            ByProvider = [new ProviderUsage(provider, totals)]
        };
    }

    private static void PumpUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Exception($"Timed out waiting for {what}");
            }

            Pump();
            Thread.Sleep(5);
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private static void Capture(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var source = PresentationSource.FromVisual(element);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1d;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);

        // A popup's content has no window behind it; paint the panel colour
        // first so the PNG shows what the user sees, then the element itself.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = element is Window window ? window.Background : (Brush)element.FindResource("WidgetBgBrush");
            context.DrawRectangle(background, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }

        bitmap.Render(visual);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
