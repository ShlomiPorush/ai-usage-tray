using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using costats.App.ViewModels;
using costats.Application.Shell;

namespace costats.App
{
    public partial class SettingsWindow : Window
    {
        private readonly IGlassBackdropService _backdropService;

        public SettingsWindow(SettingsViewModel viewModel, IGlassBackdropService backdropService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _backdropService = backdropService;
            SourceInitialized += OnSourceInitialized;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
        }

        /// <summary>
        /// Raised when the user dismisses the window, just before it is hidden,
        /// so the caller can restore whatever the settings window replaced.
        /// </summary>
        public event EventHandler? Dismissing;

        /// <summary>
        /// Set by whoever opens the window: true when the glass widget was on
        /// screen and should come back once settings is dismissed.
        /// </summary>
        public bool ReturnToWidgetOnDismiss { get; private set; }

        /// <summary>
        /// Centres the window on the work area and brings it to the front.
        /// </summary>
        public void ShowCentered(bool returnToWidgetOnDismiss)
        {
            ReturnToWidgetOnDismiss = returnToWidgetOnDismiss;
            if (DataContext is SettingsViewModel viewModel)
            {
                viewModel.RefreshUpdateAvailability();
                viewModel.RefreshUsageCacheInfo();
            }

            var workArea = SystemParameters.WorkArea;
            Left = (workArea.Width - Width) / 2 + workArea.Left;
            Top = (workArea.Height - Height) / 2 + workArea.Top;

            if (!IsVisible)
            {
                Show();
            }

            Activate();
        }

        /// <summary>
        /// Hides the window, first handing activation back to the widget when it
        /// was the caller. The order matters: the widget is shown and activated
        /// while this window is still up, so focus moves straight to the widget.
        /// Hiding first would let Windows activate some other application, and
        /// the widget's own deactivation handler would hide it again instantly.
        /// </summary>
        public void Dismiss()
        {
            if (ReturnToWidgetOnDismiss)
            {
                ReturnToWidgetOnDismiss = false;
                Dismissing?.Invoke(this, EventArgs.Empty);
            }

            Hide();
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _backdropService.ApplyBackdrop(hwnd);
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Dismiss();
        }

        private void OnAddAccountClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.SettingsViewModel viewModel)
            {
                return;
            }

            var dialog = new AddAccountWindow { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            switch (dialog.ProviderType)
            {
                case "zai":
                    viewModel.ConfigureZai(dialog.AccountName, dialog.Secret);
                    break;
                case "copilot":
                    viewModel.ConfigureCopilot(dialog.Secret);
                    break;
                default:
                    viewModel.AddAccountFromDialog(dialog.ProviderType, dialog.AccountName, dialog.ConfigDir);
                    break;
            }
        }

        private void OnEditRowClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.SettingsViewModel viewModel ||
                sender is not FrameworkElement { DataContext: ViewModels.ProviderRowViewModel row })
            {
                return;
            }

            var configDir = row.Kind is "claude" or "codex" ? row.Detail : null;
            var dialog = new AddAccountWindow(row.Kind, row.Name, configDir) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            switch (row.Kind)
            {
                case "zai":
                    viewModel.ConfigureZai(dialog.AccountName, dialog.Secret);
                    break;
                case "copilot":
                    viewModel.ConfigureCopilot(dialog.Secret);
                    break;
                default:
                    viewModel.UpdateAccountFromDialog(row.AccountId!, dialog.AccountName, dialog.ConfigDir);
                    break;
            }
        }

        private void OnUsageAlertThresholdLostFocus(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.SettingsViewModel viewModel ||
                sender is not System.Windows.Controls.TextBox
                {
                    DataContext: ViewModels.ProviderRowViewModel row
                } textBox)
            {
                return;
            }

            viewModel.SetUsageAlertThreshold(row, textBox.Text);
        }
    }
}
