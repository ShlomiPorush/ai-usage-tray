using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using costats.App.ViewModels;
using costats.Application.Settings;

namespace costats.App
{
    public enum SettingsCategory
    {
        General,
        Automation,
        Display,
        Alerts,
        Accounts,
        RemoteView,
        Updates,
        LocalUsageData
    }

    public partial class SettingsWindow : Window
    {
        private UpdateAvailableWindow? _updateAvailableWindow;

        public SettingsWindow(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            SelectCategory(SettingsCategory.General);
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            viewModel.ManualUpdateAvailable += OnManualUpdateAvailable;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        /// <summary>
        /// The import confirmation and the export/import result appear below
        /// the buttons, often past the bottom of the scroll viewport, so they
        /// are scrolled into view as soon as they show.
        /// </summary>
        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not SettingsViewModel viewModel)
            {
                return;
            }

            FrameworkElement? target = e.PropertyName switch
            {
                nameof(SettingsViewModel.IsImportConfirmationVisible) when viewModel.IsImportConfirmationVisible =>
                    ImportConfirmationPanel,
                nameof(SettingsViewModel.IsHistoryDeleteConfirmationVisible) when viewModel.IsHistoryDeleteConfirmationVisible =>
                    HistoryDeleteConfirmationPanel,
                nameof(SettingsViewModel.SettingsTransferMessage) when !string.IsNullOrEmpty(viewModel.SettingsTransferMessage) =>
                    SettingsTransferResult,
                _ => null
            };
            if (target is null)
            {
                return;
            }

            // Wait for layout so the newly visible element has its final size.
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                () => target.BringIntoView());
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
        public void ShowCentered(
            bool returnToWidgetOnDismiss,
            SettingsCategory? initialCategory = null)
        {
            ReturnToWidgetOnDismiss = returnToWidgetOnDismiss;
            if (DataContext is SettingsViewModel viewModel)
            {
                viewModel.RefreshUpdateAvailability();
                viewModel.RefreshUsageCacheInfo();
            }

            SelectCategory(initialCategory ?? SettingsCategory.General);

            // Fixed design size, clamped so the whole window fits a small or
            // highly scaled work area; the page and the navigation list scroll.
            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(DesignWidth, workArea.Width);
            Height = Math.Min(DesignHeight, workArea.Height);
            if (!IsVisible)
            {
                Show();
            }

            UpdateLayout();
            Left = (workArea.Width - ActualWidth) / 2 + workArea.Left;
            Top = (workArea.Height - ActualHeight) / 2 + workArea.Top;
            Activate();
        }

        private const double DesignWidth = 920;
        private const double DesignHeight = 660;

        private static (string Title, string Description) PageTextOf(SettingsCategory category) => category switch
        {
            SettingsCategory.General => ("General", "Startup, refresh, shortcut and moving to another computer."),
            SettingsCategory.Automation => ("Automation", "Start new 5-hour windows for you. Everything here is off by default."),
            SettingsCategory.Display => ("Display", "What the tray, panels and main window show, and how they look."),
            SettingsCategory.Alerts => ("Alerts", "Notifications when an account gets close to its limit."),
            SettingsCategory.Accounts => ("Accounts", "The accounts you monitor, and which one drives the tray icon."),
            SettingsCategory.RemoteView => ("Remote view", "A read-only page with your usage, for your phone."),
            SettingsCategory.Updates => ("Updates", "New versions of AI Usage Tray."),
            SettingsCategory.LocalUsageData => ("Local usage data", "The cache and history behind the usage reports."),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
        };

        private FrameworkElement PageOf(SettingsCategory category) => category switch
        {
            SettingsCategory.General => GeneralPage,
            SettingsCategory.Automation => AutomationPage,
            SettingsCategory.Display => DisplayPage,
            SettingsCategory.Alerts => AlertsPage,
            SettingsCategory.Accounts => AccountsPage,
            SettingsCategory.RemoteView => RemoteViewPage,
            SettingsCategory.Updates => UpdatesPage,
            SettingsCategory.LocalUsageData => LocalUsageDataPage,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
        };

        private System.Windows.Controls.RadioButton NavItemOf(SettingsCategory category) => category switch
        {
            SettingsCategory.General => GeneralNavItem,
            SettingsCategory.Automation => AutomationNavItem,
            SettingsCategory.Display => DisplayNavItem,
            SettingsCategory.Alerts => AlertsNavItem,
            SettingsCategory.Accounts => AccountsNavItem,
            SettingsCategory.RemoteView => RemoteViewNavItem,
            SettingsCategory.Updates => UpdatesNavItem,
            SettingsCategory.LocalUsageData => LocalUsageDataNavItem,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
        };

        /// <summary>
        /// Shows one category page. View state only: nothing about the selected
        /// page is saved.
        /// </summary>
        private void SelectCategory(SettingsCategory category)
        {
            foreach (var each in Enum.GetValues<SettingsCategory>())
            {
                PageOf(each).Visibility = each == category ? Visibility.Visible : Visibility.Collapsed;
            }

            var navItem = NavItemOf(category);
            if (navItem.IsChecked != true)
            {
                navItem.IsChecked = true;
            }

            var (title, description) = PageTextOf(category);
            PageTitle.Text = title;
            PageDescription.Text = description;
            SettingsScrollViewer.ScrollToTop();
        }

        private void OnNavItemChecked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.RadioButton { Tag: SettingsCategory category })
            {
                SelectCategory(category);
            }
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

        private void OnManualUpdateAvailable(object? sender, EventArgs e)
        {
            if (_updateAvailableWindow is { IsVisible: true } existingDialog)
            {
                existingDialog.Activate();
                return;
            }

            if (DataContext is not SettingsViewModel viewModel)
            {
                return;
            }

            var dialog = new UpdateAvailableWindow(viewModel) { Owner = this };
            _updateAvailableWindow = dialog;
            dialog.Closed += (_, _) =>
            {
                if (ReferenceEquals(_updateAvailableWindow, dialog))
                {
                    _updateAvailableWindow = null;
                }

                Activate();
            };
            dialog.ShowDialog();
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
                case "antigravity":
                    viewModel.ConfigureAntigravity(dialog.AntigravityIntervalMinutes);
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
            int? antigravityInterval = row.IsAntigravity ? viewModel.AntigravityMinIntervalMinutes : null;
            var dialog = new AddAccountWindow(row.Kind, row.Name, configDir, antigravityInterval) { Owner = this };
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
                case "antigravity":
                    viewModel.ConfigureAntigravity(dialog.AntigravityIntervalMinutes);
                    break;
                default:
                    viewModel.UpdateAccountFromDialog(row.AccountId!, dialog.AccountName, dialog.ConfigDir);
                    break;
            }
        }

        private async void OnExportSettingsClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.SettingsViewModel viewModel)
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export settings",
                FileName = SettingsTransfer.DefaultFileName,
                DefaultExt = ".json",
                Filter = SettingsFileFilter,
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            await viewModel.ExportSettingsAsync(dialog.FileName);
        }

        private async void OnImportSettingsClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.SettingsViewModel viewModel)
            {
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import settings",
                FileName = SettingsTransfer.DefaultFileName,
                DefaultExt = ".json",
                Filter = SettingsFileFilter,
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            await viewModel.PrepareImportAsync(dialog.FileName);
        }

        private const string SettingsFileFilter = "AI Usage Tray settings (*.json)|*.json|All files (*.*)|*.*";

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
