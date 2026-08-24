using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using costats.App.ViewModels;
using costats.Application.Shell;

namespace costats.App;

public partial class OnboardingWindow : Window
{
    private readonly OnboardingViewModel _viewModel;
    private readonly IGlassBackdropService _backdropService;
    private bool _isDismissing;
    private bool _previewOnly;

    public OnboardingWindow(OnboardingViewModel viewModel, IGlassBackdropService backdropService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _backdropService = backdropService;
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    /// <summary>Raised before the window hides so the tray can reveal the widget.</summary>
    public event EventHandler? Dismissing;

    public async void ShowCentered(bool resume, bool previewOnly = false, int previewStep = 1)
    {
        _previewOnly = previewOnly;
        await _viewModel.PrepareAsync(resume, previewOnly, previewStep).ConfigureAwait(true);

        var workArea = SystemParameters.WorkArea;
        Left = (workArea.Width - Width) / 2 + workArea.Left;
        Top = (workArea.Height - Height) / 2 + workArea.Top;
        if (!IsVisible)
        {
            Show();
        }
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        _ = DismissAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _backdropService.ApplyBackdrop(hwnd);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed &&
            e.OriginalSource is System.Windows.Controls.Border or System.Windows.Controls.Grid or Window)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private async void OnCloseClick(object sender, RoutedEventArgs e) => await DismissAsync();

    private void OnBackClick(object sender, RoutedEventArgs e) => _viewModel.Back();

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsDoneStep)
        {
            if (!_previewOnly)
            {
                await _viewModel.CompleteAsync().ConfigureAwait(true);
            }
            DismissWithoutSaving();
            return;
        }

        await _viewModel.ContinueAsync().ConfigureAwait(true);
    }

    private async Task DismissAsync()
    {
        if (_isDismissing)
        {
            return;
        }

        _isDismissing = true;
        try
        {
            if (!_previewOnly)
            {
                await _viewModel.DismissAsync().ConfigureAwait(true);
            }
            DismissWithoutSaving();
        }
        finally
        {
            _isDismissing = false;
        }
    }

    private void DismissWithoutSaving()
    {
        Dismissing?.Invoke(this, EventArgs.Empty);
        Hide();
        _previewOnly = false;
    }
}
