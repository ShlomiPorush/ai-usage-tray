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

        public GlassWidgetWindow(PulseViewModel viewModel, SettingsWindow settingsWindow, IGlassBackdropService backdropService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _backdropService = backdropService;
            _settingsWindow = settingsWindow;
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
            // Hide window when it loses focus (like a popup)
            Hide();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(PulseViewModel.IsMulticcActive) or
                nameof(PulseViewModel.SelectedTabIndex))
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
            var workArea = SystemParameters.WorkArea;
            _settingsWindow.Left = (workArea.Width - _settingsWindow.Width) / 2 + workArea.Left;
            _settingsWindow.Top = (workArea.Height - _settingsWindow.Height) / 2 + workArea.Top;

            if (!_settingsWindow.IsVisible)
            {
                _settingsWindow.Show();
            }

            _settingsWindow.Activate();
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
