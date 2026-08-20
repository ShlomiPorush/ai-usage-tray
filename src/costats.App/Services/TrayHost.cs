using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using costats.App.ViewModels;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Core.Tray;
using Microsoft.Win32;
using Serilog;

namespace costats.App.Services
{
    public sealed class TrayHost : IObserver<PulseState>, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly TaskbarIcon _taskbarIcon;
        private readonly GlassWidgetWindow _widgetWindow;
        private readonly SettingsWindow _settingsWindow;
        private readonly TrayStatusPanelWindow _trayPanel;
        private readonly IPulseOrchestrator _pulseOrchestrator;
        private readonly PulseViewModel _viewModel;
        private readonly TaskbarPositionService _taskbarPosition;
        private readonly IDisposable _pulseSubscription;
        private readonly IEnumerable<ISignalSource> _staticSources;
        private readonly IAccountSourceRegistry _accountSources;
        private Icon _currentIcon;
        private TrayStatus? _lastAppliedStatus;
        private readonly AppSettings _settings;

        public TrayHost(
            PulseViewModel viewModel,
            GlassWidgetWindow widgetWindow,
            SettingsWindow settingsWindow,
            TrayStatusPanelWindow trayPanel,
            IPulseOrchestrator pulseOrchestrator,
            TaskbarPositionService taskbarPosition,
            IEnumerable<ISignalSource> sources,
            IAccountSourceRegistry accountSources,
            AppSettings settings)
        {
            _viewModel = viewModel;
            _widgetWindow = widgetWindow;
            _settingsWindow = settingsWindow;
            _trayPanel = trayPanel;
            _pulseOrchestrator = pulseOrchestrator;
            _taskbarPosition = taskbarPosition;
            _settings = settings;
            _staticSources = sources;
            _accountSources = accountSources;

            _currentIcon = CreateIcon(TraySeverity.Unknown, null);
            _taskbarIcon = new TaskbarIcon();
            _taskbarIcon.Icon = _currentIcon;
            _taskbarIcon.ToolTipText = "AI usage is loading";
            _taskbarIcon.ContextMenu = BuildContextMenu();
            _taskbarIcon.TrayLeftMouseUp += OnTrayLeftClick;
            _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
            _pulseSubscription = pulseOrchestrator.PulseStream.Subscribe(this);

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _widgetWindow.SizeChanged += OnWidgetSizeChanged;
        }

        private void OnTrayLeftClick(object? sender, EventArgs e)
        {
            ToggleWidget();
        }

        private static Icon CreateIcon(TraySeverity severity, double? lowestRemainingPercent)
        {
            using var bitmap = new Bitmap(32, 32);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            var colour = severity switch
            {
                TraySeverity.Green => Color.FromArgb(16, 185, 129),
                TraySeverity.Amber => Color.FromArgb(245, 158, 11),
                TraySeverity.Red => Color.FromArgb(239, 68, 68),
                _ => Color.FromArgb(107, 114, 128)
            };

            using var background = new SolidBrush(colour);
            graphics.FillEllipse(background, 2, 2, 28, 28);

            // Always-on numeric overlay so the tray shows the lowest remaining
            // percentage at a glance, like the system clock. White-on-colour
            // text scales to fit small icon sizes. When there is no data, the
            // circle stays blank (no number), matching the old "loading" look.
            if (lowestRemainingPercent.HasValue)
            {
                var percentText = ((long)Math.Round(Math.Clamp(lowestRemainingPercent.Value, 0, 100))).ToString();
                using var textBrush = new SolidBrush(Color.White);
                var cellStyle = new System.Drawing.StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                // Use a smaller font for 3-digit numbers so "100" still fits.
                var fontSize = percentText.Length >= 3 ? 8 : 11;
                using var sizedFont = new System.Drawing.Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                graphics.DrawString(percentText, sizedFont, textBrush,
                    new RectangleF(2, 2, 28, 28), cellStyle);
            }

            var iconHandle = bitmap.GetHicon();
            using var temporary = Icon.FromHandle(iconHandle);
            var cloned = (Icon)temporary.Clone();
            DestroyIcon(iconHandle);
            return cloned;
        }

        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();

            var showItem = new MenuItem { Header = "Show Widget", FontWeight = FontWeights.SemiBold };
            showItem.Click += (_, _) => ShowWidget();

            var refreshItem = new MenuItem { Header = "Refresh Now" };
            refreshItem.Click += async (_, _) => await _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);

            var settingsItem = new MenuItem { Header = "Settings..." };
            settingsItem.Click += (_, _) => ShowSettings();

            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();

            menu.Items.Add(showItem);
            menu.Items.Add(refreshItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(settingsItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(exitItem);
            return menu;
        }

        public void ShowSettings()
        {
            // Center on screen
            var workArea = SystemParameters.WorkArea;
            _settingsWindow.Left = (workArea.Width - _settingsWindow.Width) / 2 + workArea.Left;
            _settingsWindow.Top = (workArea.Height - _settingsWindow.Height) / 2 + workArea.Top;

            if (!_settingsWindow.IsVisible)
            {
                _settingsWindow.Show();
            }

            _settingsWindow.Activate();
        }

        public void ShowWidget()
        {
            PositionWidget();

            var wasVisible = _widgetWindow.IsVisible;

            if (!wasVisible)
            {
                _viewModel.ResetToOverview();
                _widgetWindow.Show();
            }

            _widgetWindow.Activate();

            // Silent refresh for the currently selected provider when panel opens
            if (!wasVisible)
            {
                _ = RefreshSelectedProviderAsync().ContinueWith(
                    t => Log.Warning(t.Exception!.GetBaseException(), "Silent provider refresh failed"),
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
        }

        private Task RefreshSelectedProviderAsync()
        {
            return _viewModel.RefreshSelectedProviderSilentlyAsync();
        }

        public void HideWidget()
        {
            _widgetWindow.Hide();
        }

        public void ToggleWidget()
        {
            if (_widgetWindow.IsVisible)
            {
                HideWidget();
            }
            else
            {
                ShowWidget();
            }
        }

        public void OnNext(PulseState state)
        {
            var displayNames = _staticSources
                .Concat(_accountSources.Current)
                .Select(source => source.Profile)
                .GroupBy(profile => profile.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);

            var accounts = state.Providers
                .Where(pair =>
                    pair.Key.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals("zai", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.StartsWith("claude:", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.StartsWith("codex:", StringComparison.OrdinalIgnoreCase))
                .Select(pair =>
                {
                    var label = displayNames.TryGetValue(pair.Key, out var displayName)
                        ? displayName
                        : pair.Key;
                    return pair.Value.Usage is { } usage
                        ? AccountUsageStatus.FromUsagePulse(label, usage)
                        : new AccountUsageStatus(label, null, null, null, null);
                })
                .OrderBy(account => account.Label.StartsWith("Claude", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(account => account.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var status = TrayStatusComposer.Compose(accounts, DateTimeOffset.UtcNow);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            dispatcher.BeginInvoke(() => ApplyTrayStatus(status));
        }

        public void OnError(Exception error)
        {
            Log.Warning(error, "Tray usage stream failed");
        }

        public void OnCompleted()
        {
        }

        private void ApplyTrayStatus(TrayStatus status)
        {
            _taskbarIcon.ToolTipText = string.IsNullOrWhiteSpace(status.Tooltip)
                ? "No AI usage data available"
                : status.Tooltip;

            // Only redraw the icon when the severity or percent actually changed,
            // so the tray isn't constantly invalidated every refresh.
            var severityChanged = _lastAppliedStatus is null
                || _lastAppliedStatus.Severity != status.Severity
                || Math.Abs((_lastAppliedStatus.LowestRemainingPercent ?? -1) - (status.LowestRemainingPercent ?? -1)) >= 1;
            if (severityChanged)
            {
                var replacement = CreateIcon(status.Severity, status.LowestRemainingPercent);
                var previous = _currentIcon;
                _currentIcon = replacement;
                _taskbarIcon.Icon = replacement;
                previous.Dispose();
            }
            _lastAppliedStatus = status;

            // Always-on top panel next to the clock. Sized first, then positioned
            // so its right edge sits flush against the tray area. Gated by
            // AppSettings.ShowClockPanel — off by default.
            var panelText = status.Tooltip;
            if (string.IsNullOrWhiteSpace(panelText) || !_settings.ShowClockPanel)
            {
                _trayPanel.HidePanel();
            }
            else
            {
                _trayPanel.UpdateMeasure();
                var size = _trayPanel.GetDesiredSize();
                var pos = _taskbarPosition.GetTrayPanelPosition(size.Width, size.Height);
                _trayPanel.Update(panelText, pos.X, pos.Y);
            }
        }

        public void Dispose()
        {
            _pulseSubscription.Dispose();
            _widgetWindow.SizeChanged -= OnWidgetSizeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _taskbarIcon.Dispose();
            _currentIcon.Dispose();
            _widgetWindow.Close();
            _settingsWindow.Close();
            _trayPanel.Close();
        }

        private void OnWidgetSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_widgetWindow.IsVisible)
            {
                PositionWidget();
            }
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            if (_widgetWindow.IsVisible)
            {
                PositionWidget();
            }
            // Force the next refresh to re-anchor the tray panel to the
            // (possibly new) bottom-right corner.
            _lastAppliedStatus = null;
        }

        private void PositionWidget()
        {
            var position = _taskbarPosition.GetWidgetPosition(_widgetWindow.Width, _widgetWindow.Height, 12);
            _widgetWindow.Left = position.X;
            _widgetWindow.Top = position.Y;
        }
    }
}
