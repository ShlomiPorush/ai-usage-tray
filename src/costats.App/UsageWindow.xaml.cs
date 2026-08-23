using System.ComponentModel;
using System.Windows;
using costats.App.ViewModels;
using costats.Application.Windowing;

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
        /// Makes the custom title bar reachable before every show. WPF sizes
        /// this window in DIPs, so a fixed 900-DIP height can be taller than a
        /// laptop work area at 125% or 150% display scaling.
        /// </summary>
        private void RestoreAndFitToWorkArea()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            if (WindowState != WindowState.Normal)
            {
                return;
            }

            // After the safe first placement, keep any move or resize the user
            // makes for the lifetime of the app.
            if (_hasBeenPositioned)
            {
                return;
            }

            var area = SystemParameters.WorkArea;
            var fitted = WindowPlacementCalculator.FitCentered(
                new WindowBounds(area.Left, area.Top, area.Width, area.Height),
                desiredWidth: 1180,
                desiredHeight: 900,
                minWidth: MinWidth,
                minHeight: MinHeight);

            Width = fitted.Width;
            Height = fitted.Height;
            Left = fitted.Left;
            Top = fitted.Top;
            _hasBeenPositioned = true;
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
