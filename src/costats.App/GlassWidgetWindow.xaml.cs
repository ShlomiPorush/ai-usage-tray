using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Navigation;
using costats.App.ViewModels;
using costats.Application.Shell;

namespace costats.App
{
    public partial class GlassWidgetWindow : Window
    {
        private readonly IGlassBackdropService _backdropService;
        private readonly SettingsWindow _settingsWindow;
        private readonly UsageWindow _usageWindow;
        private readonly OnboardingWindow _onboardingWindow;

        public GlassWidgetWindow(
            PulseViewModel viewModel,
            SettingsWindow settingsWindow,
            UsageWindow usageWindow,
            OnboardingWindow onboardingWindow,
            IGlassBackdropService backdropService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _backdropService = backdropService;
            _settingsWindow = settingsWindow;
            _usageWindow = usageWindow;
            _onboardingWindow = onboardingWindow;
            SourceInitialized += OnSourceInitialized;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            Deactivated += OnDeactivated;

            // Subscribe to ViewModel property changes for dynamic height
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            // Skip backdrop - we use AllowsTransparency with custom Border for rounded corners
            // Applying DWM backdrop creates a conflicting layer with different corner radius
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Allow dragging the window, but only if clicking on the background (not on buttons/controls)
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is System.Windows.Controls.Border or System.Windows.Controls.Grid or Window)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // DragMove can throw if called at wrong time
                }
            }
        }

        private void OnDeactivated(object? sender, EventArgs e)
        {
            // The main widget remains a tray popup. The optional always-on
            // surface is the separate compact status panel.
            Hide();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PulseViewModel.IsOverview))
            {
                UpdateWindowHeight();
            }
        }

        private void UpdateWindowHeight()
        {
            // The window sizes to its content (all account cards visible); the
            // work area caps it so it never grows past the screen, at which
            // point the overview scrolls.
            MaxHeight = SystemParameters.WorkArea.Height - 24;
        }

        private void OnQuitClick(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            // The widget hides itself as soon as settings takes focus, so ask
            // settings to bring it back when the user dismisses it.
            _settingsWindow.ShowCentered(returnToWidgetOnDismiss: true);
        }

        private void OnReloginClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PulseViewModel pulse ||
                _settingsWindow.DataContext is not SettingsViewModel settings)
            {
                return;
            }

            var providerId = pulse.SelectedAccount.ProviderId;
            var row = settings.ProviderRows.FirstOrDefault(candidate =>
                string.Equals(candidate.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (row is not null && settings.ReloginProviderRowCommand.CanExecute(row))
            {
                settings.ReloginProviderRowCommand.Execute(row);
            }
        }

        private void OnUsageStatsClick(object sender, RoutedEventArgs e)
        {
            // The widget hides itself the moment the dashboard takes focus,
            // which is what we want: the dashboard is a full window.
            _usageWindow.ShowUsage();
        }

        private void OnFinishSetupClick(object sender, RoutedEventArgs e)
        {
            _onboardingWindow.ShowCentered(resume: true);
        }

        private void OnViewUpdateClick(object sender, RoutedEventArgs e)
        {
            _settingsWindow.ShowCentered(
                returnToWidgetOnDismiss: true,
                initialCategory: SettingsCategory.Updates);
        }

        private void OnAccountUsageClick(object sender, RoutedEventArgs e)
        {
            // The Cost section only exists once an analytics bucket was
            // resolved, so the id is set by the time this button is clickable.
            if (sender is FrameworkElement { DataContext: ProviderPulseViewModel account } &&
                !string.IsNullOrWhiteSpace(account.UsageAccountId))
            {
                _usageWindow.ShowUsageForAccount(account.UsageAccountId);
            }
        }

        private void OnUsageLinkNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }
    }
}
