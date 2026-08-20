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

        private void OnBrowseConfigDirClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: AccountEditorViewModel account })
            {
                return;
            }

            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select the account's profile folder",
                InitialDirectory = System.IO.Directory.Exists(account.ConfigDir)
                    ? account.ConfigDir
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            if (dialog.ShowDialog(this) == true)
            {
                account.ConfigDir = dialog.FolderName;
            }
        }

        private async void OnSaveCopilotTokenClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel viewModel)
            {
                var token = CopilotTokenBox.Password;
                await viewModel.SaveCopilotTokenAsync(token);
            }
        }
    }
}
