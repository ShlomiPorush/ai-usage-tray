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
            Hide();
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
    }
}
