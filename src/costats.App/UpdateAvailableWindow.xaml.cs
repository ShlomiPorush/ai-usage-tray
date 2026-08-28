using System.Windows;
using System.Windows.Input;
using costats.App.ViewModels;

namespace costats.App;

public partial class UpdateAvailableWindow : Window
{
    public UpdateAvailableWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        MaxHeight = SystemParameters.WorkArea.Height;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
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
        Close();
    }

    private void OnLaterClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
