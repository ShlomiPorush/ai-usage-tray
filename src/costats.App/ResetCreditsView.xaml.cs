using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using costats.App.ViewModels;

namespace costats.App;

public partial class ResetCreditsView : UserControl
{
    public event EventHandler? BackRequested;

    public ResetCreditsView()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is ResetCreditsViewModel previous) previous.PropertyChanged -= OnViewModelChanged;
            if (args.NewValue is ResetCreditsViewModel current) current.PropertyChanged += OnViewModelChanged;
        };
        // Card clicks should select a reset, not start dragging the widget.
        MouseLeftButtonDown += (_, args) => args.Handled = true;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResetCreditsViewModel.IsConfirming) &&
            DataContext is ResetCreditsViewModel { IsConfirming: true })
        {
            // The confirmation reduces the list viewport. Keep the chosen row in view.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (CreditsList.SelectedItem is { } selected) CreditsList.ScrollIntoView(selected);
            });
        }
    }
}
