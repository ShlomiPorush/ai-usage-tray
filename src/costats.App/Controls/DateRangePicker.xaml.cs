using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using costats.Core.Analytics;

namespace costats.App.Controls;

/// <summary>One day of the picker's month grid.</summary>
public sealed partial class DateRangePickerDay : ObservableObject
{
    /// <summary>Creates a cell for <paramref name="date"/>.</summary>
    public DateRangePickerDay(DateOnly date, bool isOutsideMonth, bool isToday, bool isSelectable, bool isReference)
    {
        Date = date;
        IsOutsideMonth = isOutsideMonth;
        IsToday = isToday;
        IsSelectable = isSelectable;
        IsReference = isReference;
    }

    /// <summary>The day the cell stands for.</summary>
    public DateOnly Date { get; }

    /// <summary>The day number shown in the cell.</summary>
    public string Text => Date.Day.ToString(CultureInfo.InvariantCulture);

    /// <summary>True for the leading and trailing days of the neighbouring months.</summary>
    public bool IsOutsideMonth { get; }

    /// <summary>True for today.</summary>
    public bool IsToday { get; }

    /// <summary>False for days that cannot be picked.</summary>
    public bool IsSelectable { get; }

    /// <summary>True for days of the reference range, drawn underlined.</summary>
    public bool IsReference { get; }

    /// <summary>What a screen reader announces: "Sat, Aug 23, 2026".</summary>
    public string AutomationName => Date.ToString("ddd, MMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));

    /// <summary>True inside the pending selection.</summary>
    [ObservableProperty]
    private bool isInRange;

    /// <summary>True for the first or last selected day.</summary>
    [ObservableProperty]
    private bool isEndpoint;
}

/// <summary>
/// A themed month grid for picking a range of days, or a single day, for the
/// Usage window. The pending selection stays here until the owner reads it on
/// Apply, so cancelling a popup never touches the period on screen.
/// </summary>
/// <remarks>
/// The selection rules (two plain clicks make a range, shift-click closes it
/// against the anchor, disallowed days are ignored) live in
/// <see cref="DateRangeSelection"/>, where they are unit tested.
/// </remarks>
public partial class DateRangePicker : UserControl
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    private readonly ObservableCollection<DateRangePickerDay> _days = [];
    private DateRangeSelection _selection = new(false, 1, _ => false);
    private DateOnly _month;
    private DateOnly _today;
    private DateOnly _min;
    private DateOnly _max;
    private (DateOnly From, DateOnly To)? _reference;

    /// <summary>Creates an empty picker; call <see cref="Reset"/> before showing it.</summary>
    public DateRangePicker()
    {
        InitializeComponent();
        DayGrid.ItemsSource = _days;
        WeekdayHeader.ItemsSource = WeekdayNames();
    }

    /// <summary>Raised whenever the pending selection changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>First picked day, or null.</summary>
    public DateOnly? From => _selection.From;

    /// <summary>Last picked day, or null.</summary>
    public DateOnly? To => _selection.To;

    /// <summary>True when a range click is still waiting for its second end.</summary>
    public bool IsAwaitingEnd => _selection.IsAwaitingEnd;

    /// <summary>Starts a fresh pending selection and shows the month it begins in.</summary>
    /// <param name="singleDate">True to pick one day instead of a range.</param>
    /// <param name="today">Today, marked in the grid.</param>
    /// <param name="min">Earliest day that can be shown and picked.</param>
    /// <param name="max">Latest day that can be shown and picked.</param>
    /// <param name="maxSpanDays">Longest range allowed.</param>
    /// <param name="isAllowed">Extra rule for which days can be picked, or null.</param>
    /// <param name="from">The selection to start from, or null.</param>
    /// <param name="to">End of the starting selection.</param>
    /// <param name="reference">Days to underline for orientation, or null.</param>
    /// <param name="shownDay">A day in the month to show when nothing is selected.</param>
    public void Reset(
        bool singleDate,
        DateOnly today,
        DateOnly min,
        DateOnly max,
        int maxSpanDays,
        Func<DateOnly, bool>? isAllowed,
        DateOnly? from,
        DateOnly? to,
        (DateOnly From, DateOnly To)? reference = null,
        DateOnly? shownDay = null)
    {
        _today = today;
        _min = min;
        _max = max;
        _reference = reference;
        _selection = new DateRangeSelection(
            singleDate,
            maxSpanDays,
            day => day >= _min && day <= _max && (isAllowed?.Invoke(day) ?? true),
            from,
            to);

        var shown = to ?? from ?? shownDay ?? today;
        ShowMonth(new DateOnly(shown.Year, shown.Month, 1));
    }

    /// <summary>Moves keyboard focus to the selected day, or today, or the first pickable day.</summary>
    public void FocusSelection()
    {
        var target = _days.FirstOrDefault(day => day.IsSelectable && day.Date == (_selection.From ?? _today))
            ?? _days.FirstOrDefault(day => day.IsSelectable && !day.IsOutsideMonth);
        if (target is null)
        {
            return;
        }

        DayGrid.UpdateLayout();
        if (DayGrid.ItemContainerGenerator.ContainerFromItem(target) is DependencyObject container &&
            FindButton(container) is { } button)
        {
            button.Focus();
            Keyboard.Focus(button);
        }
    }

    private void ShowMonth(DateOnly month)
    {
        var first = new DateOnly(_min.Year, _min.Month, 1);
        var last = new DateOnly(_max.Year, _max.Month, 1);
        _month = month < first ? first : month > last ? last : month;

        MonthTitle.Text = _month.ToString("MMMM yyyy", Culture);
        PreviousMonthButton.IsEnabled = _month > first;
        NextMonthButton.IsEnabled = _month < last;

        _days.Clear();
        foreach (var date in DateRangeSelection.MonthGrid(_month.Year, _month.Month, FirstDayOfWeek))
        {
            _days.Add(new DateRangePickerDay(
                date,
                isOutsideMonth: date.Month != _month.Month,
                isToday: date == _today,
                isSelectable: _selection.IsAllowed(date),
                isReference: _reference is { } reference && date >= reference.From && date <= reference.To));
        }

        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var day in _days)
        {
            day.IsInRange = _selection.Contains(day.Date);
            day.IsEndpoint = day.Date == _selection.From || day.Date == _selection.To;
        }
    }

    private void OnDayClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DateRangePickerDay day })
        {
            return;
        }

        var extend = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (!_selection.Click(day.Date, extend))
        {
            return;
        }

        if (day.IsOutsideMonth)
        {
            ShowMonth(new DateOnly(day.Date.Year, day.Date.Month, 1));
            FocusSelection();
        }
        else
        {
            RefreshSelection();
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreviousMonthClick(object sender, RoutedEventArgs e) => ShowMonth(_month.AddMonths(-1));

    private void OnNextMonthClick(object sender, RoutedEventArgs e) => ShowMonth(_month.AddMonths(1));

    /// <summary>The week starts where the user's regional settings say it does.</summary>
    private static DayOfWeek FirstDayOfWeek => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    private static IReadOnlyList<string> WeekdayNames()
    {
        var names = Culture.DateTimeFormat.ShortestDayNames;
        return Enumerable.Range(0, 7)
            .Select(offset => names[((int)FirstDayOfWeek + offset) % 7])
            .ToList();
    }

    private static Button? FindButton(DependencyObject root)
    {
        if (root is Button button)
        {
            return button;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindButton(VisualTreeHelper.GetChild(root, index)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
