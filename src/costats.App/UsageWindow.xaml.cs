using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using costats.App.ViewModels;
using costats.Application.Windowing;
using costats.Core.Analytics;

namespace costats.App
{
    /// <summary>
    /// The Usage dashboard: raw token cost, a daily chart and a model or day
    /// breakdown of everything the local agent logs hold.
    /// </summary>
    /// <remarks>
    /// One instance for the whole app, like <see cref="SettingsWindow"/>:
    /// closing hides it so reopening is instant and keeps the loaded report.
    /// Nothing here is persisted; the window remembers no state between runs.
    /// </remarks>
    public partial class UsageWindow : Window
    {
        private readonly UsageWindowViewModel _viewModel;
        private bool _allowClose;
        private bool _hasBeenPositioned;

        /// <summary>
        /// What a maximized WindowChrome window has to give back. The window is
        /// sized to the monitor plus its resize border, so the content needs the
        /// same amount of padding or it runs off the screen edges and under the
        /// taskbar.
        /// </summary>
        private static readonly Thickness MaximizedPadding = new(7);

        /// <summary>Creates the window over its view model.</summary>
        public UsageWindow(UsageWindowViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
            StateChanged += (_, _) => ApplyMaximizedPadding();
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            PreviewKeyDown += OnWindowPreviewKeyDown;
        }

        private IInputElement? _focusBeforeDetail;
        private bool _detailShown;

        /// <summary>
        /// Moves focus into the model details when they open, and back to the
        /// row that opened them when they close, so the keyboard never ends up
        /// behind the dimmed area. A reload or a metric switch replaces the
        /// details while they stay open; that is not an opening.
        /// </summary>
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(UsageWindowViewModel.IsModelDetailOpen) ||
                _viewModel.IsModelDetailOpen == _detailShown)
            {
                return;
            }

            _detailShown = _viewModel.IsModelDetailOpen;
            if (_detailShown)
            {
                _focusBeforeDetail = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ModelDetailCloseButton.Focus()));
                return;
            }

            ModelChart.ShowHover(null);
            if (_focusBeforeDetail is UIElement { IsVisible: true } previous)
            {
                previous.Focus();
            }

            _focusBeforeDetail = null;
        }

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _viewModel.IsModelDetailOpen)
            {
                _viewModel.CloseModelDetailCommand.Execute(null);
                e.Handled = true;
            }
        }

        private void OnModelScrimMouseDown(object sender, MouseButtonEventArgs e)
        {
            _viewModel.CloseModelDetailCommand.Execute(null);
            e.Handled = true;
        }

        private void ApplyMaximizedPadding() =>
            RootGrid.Margin = WindowState == WindowState.Maximized ? MaximizedPadding : default;

        private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                SystemCommands.RestoreWindow(this);
            }
            else
            {
                SystemCommands.MaximizeWindow(this);
            }
        }

        // Closing is cancelled in OnClosing: the window hides and keeps its
        // loaded report, exactly like the native close button did.
        private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

        /// <summary>
        /// Shows the window, or focuses and refreshes it when it is already up.
        /// The load runs in the background, so the window paints immediately.
        /// </summary>
        public void ShowUsage()
        {
            RestoreAndFitToWorkArea();

            if (!IsVisible)
            {
                Show();
            }

            Activate();
            _ = _viewModel.InitializeAsync();
        }

        /// <summary>
        /// Shows the window filtered to one analytics account, for callers that
        /// arrive from a single account's panel. Codex accounts must pass the
        /// merged Codex bucket id: the logs cannot be split per profile.
        /// </summary>
        public void ShowUsageForAccount(string accountId)
        {
            RestoreAndFitToWorkArea();

            if (!IsVisible)
            {
                Show();
            }

            Activate();
            _ = _viewModel.InitializeForAccountAsync(accountId);
        }

        /// <summary>
        /// Makes the custom title bar reachable before the first show while
        /// preserving any later move or resize for the rest of the app session.
        /// </summary>
        private void RestoreAndFitToWorkArea()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            if (WindowState != WindowState.Normal || _hasBeenPositioned)
            {
                return;
            }

            var area = SystemParameters.WorkArea;
            var placement = WindowPlacementCalculator.FitCentered(
                new WindowBounds(area.Left, area.Top, area.Width, area.Height),
                Width,
                Height,
                MinWidth,
                MinHeight);

            MinWidth = placement.MinWidth;
            MinHeight = placement.MinHeight;
            Width = placement.Bounds.Width;
            Height = placement.Bounds.Height;
            Left = placement.Bounds.Left;
            Top = placement.Bounds.Top;
            _hasBeenPositioned = true;
        }

        private void OnCustomRangeClick(object sender, RoutedEventArgs e)
        {
            var today = _viewModel.Today;
            var (from, to) = _viewModel.IsCustomRange
                ? (_viewModel.CustomFrom, _viewModel.CustomTo)
                : _viewModel.SelectedDays();

            RangePicker.Reset(
                singleDate: false,
                today,
                today.AddDays(-UsagePeriods.MaxLookbackDays),
                today,
                UsagePeriods.MaxCustomDays,
                isAllowed: null,
                from,
                to);
            UpdateRangeSelectionText();
            RangePopup.IsOpen = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(RangePicker.FocusSelection));
        }

        private void OnRangePickerChanged(object? sender, EventArgs e) => UpdateRangeSelectionText();

        private void UpdateRangeSelectionText()
        {
            var today = _viewModel.Today;
            RangeApplyButton.IsEnabled = RangePicker.From is not null && RangePicker.To is not null;
            RangeSelectionText.Text = RangePicker.From is { } from && RangePicker.To is { } to
                ? RangePicker.IsAwaitingEnd
                    ? $"{UsageNumberFormat.RangeLabel(from, to, today)}, now pick the last day"
                    : $"{UsageNumberFormat.RangeLabel(from, to, today)} ({DayCount(from, to)})"
                : "No days picked";
        }

        private void OnRangeApplyClick(object sender, RoutedEventArgs e)
        {
            if (RangePicker.From is { } from && RangePicker.To is { } to)
            {
                _viewModel.ApplyCustomRange(from, to);
            }

            RangePopup.IsOpen = false;
        }

        private void OnRangeCancelClick(object sender, RoutedEventArgs e) => RangePopup.IsOpen = false;

        private (DateOnly From, DateOnly To)? _periodA;
        private (DateOnly From, DateOnly To)? _periodB;
        private bool _periodAEdited;
        private bool _periodBEdited;
        private bool _periodBIsPrevious;
        private bool _switchingPeriod;

        /// <summary>
        /// Opens the comparison picker with period A on the selected range and
        /// period B on the current comparison, or the period just before A.
        /// </summary>
        private void OnCompareClick(object sender, RoutedEventArgs e)
        {
            var picked = _viewModel.IsComparing ? _viewModel.ComparisonRange : null;
            _periodA = _viewModel.SelectedDays();
            _periodB = picked ?? _viewModel.PreviousPeriodDays();
            _periodAEdited = false;
            _periodBEdited = false;
            _periodBIsPrevious = picked is null;

            CompareOffButton.Visibility = _viewModel.IsComparing ? Visibility.Visible : Visibility.Collapsed;
            SelectComparePeriod(editA: true);
            ComparePopup.IsOpen = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ComparePicker.FocusSelection));
        }

        private void OnComparePeriodChecked(object sender, RoutedEventArgs e)
        {
            if (!_switchingPeriod)
            {
                SelectComparePeriod(editA: ReferenceEquals(sender, PeriodAOption));
            }
        }

        /// <summary>Points the calendar at one period and underlines the other.</summary>
        private void SelectComparePeriod(bool editA)
        {
            _switchingPeriod = true;
            try
            {
                PeriodAOption.IsChecked = editA;
                PeriodBOption.IsChecked = !editA;
            }
            finally
            {
                _switchingPeriod = false;
            }

            var today = _viewModel.Today;
            var editing = editA ? _periodA : _periodB;
            var other = editA ? _periodB : _periodA;
            ComparePicker.Reset(
                singleDate: false,
                today,
                today.AddDays(-UsagePeriods.MaxLookbackDays),
                today,
                UsagePeriods.MaxCustomDays,
                isAllowed: null,
                editing?.From,
                editing?.To,
                reference: other);

            UpdateCompareSelection();
        }

        private void OnComparePickerChanged(object? sender, EventArgs e)
        {
            // A period only changes once both of its days are picked. A first
            // click alone is shown as pending and is dropped if the user moves
            // to the other period, so Apply can never use half a selection.
            if (ComparePicker.IsAwaitingEnd)
            {
                UpdateCompareSelection();
                return;
            }

            if (ComparePicker.From is not { } from || ComparePicker.To is not { } to)
            {
                return;
            }

            var editA = PeriodAOption.IsChecked == true;
            if (editA)
            {
                _periodA = (from, to);
                _periodAEdited = true;

                // An untouched period B is "the period before A", so it moves
                // along with A instead of keeping dates that no longer fit.
                if (_periodBIsPrevious && !_periodBEdited)
                {
                    var length = to.DayNumber - from.DayNumber + 1;
                    _periodB = (from.AddDays(-length), from.AddDays(-1));
                }
            }
            else
            {
                _periodB = (from, to);
                _periodBEdited = true;
            }

            // Once period A is complete the calendar moves on to period B, so
            // picking both is four clicks with nothing else in between.
            if (editA)
            {
                SelectComparePeriod(editA: false);
                return;
            }

            UpdateCompareSelection();
        }

        private void UpdateCompareSelection()
        {
            if (PeriodAText is null || PeriodBText is null || CompareHintText is null || CompareLengthNote is null)
            {
                return;
            }

            // Untouched defaults are described the way they will be applied:
            // "Past 24h" stays a rolling window, and so does the one before it.
            var today = _viewModel.Today;
            var bFollowsA = _periodBIsPrevious && !_periodBEdited;
            PeriodAText.Text = _periodAEdited
                ? DescribePeriod(_periodA, today)
                : $"{_viewModel.DescribeSelectedRange()}\n{DayCountText(_viewModel.SelectedDayCount)}";
            PeriodBText.Text = bFollowsA && !_periodAEdited
                ? $"{_viewModel.DescribePreviousPeriod()}\n{DayCountText(_viewModel.SelectedDayCount)}"
                : DescribePeriod(_periodB, today);

            var editA = PeriodAOption.IsChecked == true;
            if (ComparePicker.IsAwaitingEnd && ComparePicker.From is { } pending)
            {
                var pendingText = $"{UsageNumberFormat.DayLabel(pending)} to ...\npick the last day";
                if (editA)
                {
                    PeriodAText.Text = pendingText;
                }
                else
                {
                    PeriodBText.Text = pendingText;
                }
            }

            CompareHintText.Text = ComparePicker.IsAwaitingEnd
                ? $"Now click the last day of period {(editA ? "A" : "B")}."
                : $"Editing period {(editA ? "A" : "B")}. Underlined days are period {(editA ? "B" : "A")}.";

            var lengthA = _periodAEdited ? Length(_periodA) : _viewModel.SelectedDayCount;
            var lengthB = bFollowsA ? lengthA : Length(_periodB);
            CompareLengthNote.Visibility = lengthA > 0 && lengthB > 0 && lengthA != lengthB
                ? Visibility.Visible
                : Visibility.Collapsed;
            CompareApplyButton.IsEnabled = _periodA is not null && _periodB is not null && !ComparePicker.IsAwaitingEnd;
        }

        private void OnCompareApplyClick(object sender, RoutedEventArgs e)
        {
            if (_periodA is not { } a || _periodB is not { } b)
            {
                return;
            }

            // Untouched defaults keep their exact meaning: the range on screen
            // stays as it is (so "Past 24h" stays rolling), and an untouched
            // period B stays "the period before", which follows the range.
            _viewModel.ApplyComparisonPeriods(
                _periodAEdited ? a : null,
                (_periodBEdited || !_periodBIsPrevious) ? b : null);
            ComparePopup.IsOpen = false;
        }

        private void OnCompareOffClick(object sender, RoutedEventArgs e)
        {
            _viewModel.ApplyComparison(enabled: false);
            ComparePopup.IsOpen = false;
        }

        private static string DescribePeriod((DateOnly From, DateOnly To)? period, DateOnly today) =>
            period is { } days
                ? $"{UsageNumberFormat.RangeLabel(days.From, days.To, today)}\n{DayCountText(Length(period))}"
                : "Not picked";

        private static int Length((DateOnly From, DateOnly To)? period) =>
            period is { } days ? days.To.DayNumber - days.From.DayNumber + 1 : 0;

        private static string DayCountText(int days) => days == 1 ? "1 day" : $"{days} days";

        private void OnCompareCancelClick(object sender, RoutedEventArgs e) => ComparePopup.IsOpen = false;

        private void OnPopupKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && sender is Popup popup)
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
        }

        private static string DayCount(DateOnly from, DateOnly to)
        {
            var days = to.DayNumber - from.DayNumber + 1;
            return days == 1 ? "by hour" : $"{days} days";
        }

        /// <summary>Closes the window for real, on application shutdown.</summary>
        public void ForceClose()
        {
            _allowClose = true;
            Close();
        }

        /// <inheritdoc />
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }

            base.OnClosing(e);
        }
    }
}
