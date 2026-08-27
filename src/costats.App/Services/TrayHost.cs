using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using costats.App.ViewModels;
using costats.App.Services.Updates;
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
        private readonly TaskbarIcon _taskbarIcon;
        private readonly GlassWidgetWindow _widgetWindow;
        private readonly SettingsWindow _settingsWindow;
        private readonly OnboardingWindow _onboardingWindow;
        private readonly UsageWindow _usageWindow;
        private readonly TrayStatusPanelWindow _statusPanelWindow;
        private readonly IPulseOrchestrator _pulseOrchestrator;
        private readonly PulseViewModel _viewModel;
        private readonly TaskbarPositionService _taskbarPosition;
        private readonly IDisposable _pulseSubscription;
        private readonly IEnumerable<ISignalSource> _staticSources;
        private readonly IAccountSourceRegistry _accountSources;
        private Icon _currentIcon;
        private System.Windows.Controls.StackPanel _tooltipPanel = null!;
        private Window? _hoverTooltipWindow;
        private System.Windows.Threading.DispatcherTimer? _hoverHideTimer;
        private readonly TrayHoverRetention _hoverRetention = new(TimeSpan.FromMilliseconds(300));
        private TrayStatus? _lastAppliedStatus;
        private bool _lastShowRemainingPercentages;
        private readonly AppSettings _settings;

        public TrayHost(
            PulseViewModel viewModel,
            GlassWidgetWindow widgetWindow,
            SettingsWindow settingsWindow,
            OnboardingWindow onboardingWindow,
            UsageWindow usageWindow,
            TrayStatusPanelWindow statusPanelWindow,
            IPulseOrchestrator pulseOrchestrator,
            TaskbarPositionService taskbarPosition,
            IEnumerable<ISignalSource> sources,
            IAccountSourceRegistry accountSources,
            AppSettings settings)
        {
            _viewModel = viewModel;
            _widgetWindow = widgetWindow;
            _settingsWindow = settingsWindow;
            _onboardingWindow = onboardingWindow;
            _usageWindow = usageWindow;
            _statusPanelWindow = statusPanelWindow;
            _pulseOrchestrator = pulseOrchestrator;
            _taskbarPosition = taskbarPosition;
            _settings = settings;
            _staticSources = sources;
            _accountSources = accountSources;

            _currentIcon = CreateIcon(TraySeverity.Unknown, null);
            _taskbarIcon = new TaskbarIcon();
            _taskbarIcon.Icon = _currentIcon;
            _taskbarIcon.ToolTipText = "AI usage is loading";
            _tooltipPanel = new System.Windows.Controls.StackPanel();
            var loadingText = new System.Windows.Controls.TextBlock { FontSize = 12, Text = "AI usage is loading" };
            loadingText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
            _tooltipPanel.Children.Add(loadingText);
            var tooltipBorder = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 9, 12, 9),
                Child = _tooltipPanel
            };
            tooltipBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PanelBgBrush");
            tooltipBorder.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "WindowBorderBrush");

            // The shell tooltip is capped at 127 characters and custom
            // TrayToolTip elements are ignored on modern Windows, so hovering
            // shows our own borderless popup with the full account list instead.
            _taskbarIcon.ToolTipText = string.Empty;
            _hoverTooltipWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowActivated = false,
                Focusable = false,
                IsHitTestVisible = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Left = -10000,
                Top = -10000,
                Content = tooltipBorder
            };
            _hoverHideTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _hoverHideTimer.Tick += (_, _) =>
            {
                if (_widgetWindow.IsVisible)
                {
                    _hoverHideTimer!.Stop();
                    _hoverTooltipWindow!.Hide();
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                var pointerIsOverTrayIcon = IsPointerOverTrayIcon();
                if (pointerIsOverTrayIcon)
                {
                    _hoverRetention.MarkTrayActivity(now);
                }

                if (!_hoverRetention.ShouldRemainVisible(now, pointerIsOverTrayIcon))
                {
                    _hoverHideTimer!.Stop();
                    _hoverTooltipWindow!.Hide();
                }
            };
            _taskbarIcon.TrayMouseMove += OnTrayMouseMove;
            _taskbarIcon.ContextMenu = BuildContextMenu();
            _taskbarIcon.TrayLeftMouseUp += OnTrayLeftClick;
            _taskbarIcon.TrayBalloonTipClicked += (_, _) => ShowSettings();
            _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
            _pulseSubscription = pulseOrchestrator.PulseStream.Subscribe(this);

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _widgetWindow.SizeChanged += OnWidgetSizeChanged;
            _settingsWindow.Dismissing += OnSettingsDismissing;
            _onboardingWindow.Dismissing += OnOnboardingDismissing;
        }

        private void OnTrayLeftClick(object? sender, EventArgs e)
        {
            _hoverHideTimer?.Stop();
            _hoverTooltipWindow?.Hide();
            ToggleWidget();
            RaiseFloatingPanel();
        }

        private void OnTrayMouseMove(object? sender, EventArgs e)
        {
            if (_hoverTooltipWindow is null || _widgetWindow.IsVisible)
            {
                RaiseFloatingPanel();
                return;
            }

            _hoverRetention.MarkTrayActivity(DateTimeOffset.UtcNow);

            if (!_hoverTooltipWindow.IsVisible)
            {
                _hoverTooltipWindow.Show();
            }

            _hoverTooltipWindow.UpdateLayout();

            // Anchor to the tray icon itself: centred on the cursor's X, sitting
            // just above the taskbar (the cursor is inside the taskbar on hover).
            GetCursorPos(out var cursor);
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_hoverTooltipWindow);
            var cursorX = cursor.X / dpi.DpiScaleX;
            var workArea = SystemParameters.WorkArea;

            var left = cursorX - _hoverTooltipWindow.ActualWidth / 2;
            left = Math.Max(workArea.Left + 4, Math.Min(left, workArea.Right - _hoverTooltipWindow.ActualWidth - 4));
            _hoverTooltipWindow.Left = left;
            _hoverTooltipWindow.Top = workArea.Bottom - _hoverTooltipWindow.ActualHeight - 6;

            // Poll the actual icon rectangle so a stationary pointer keeps the
            // popup open; Windows does not keep sending mouse-move events.
            _hoverHideTimer!.Stop();
            _hoverHideTimer.Start();
            RaiseFloatingPanel();
        }

        private bool IsPointerOverTrayIcon()
        {
            if (!GetCursorPos(out var cursor))
            {
                return false;
            }

            var identifier = new NotifyIconIdentifier
            {
                CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NotifyIconIdentifier>(),
                HWnd = _taskbarIcon.TrayIcon.WindowHandle,
                UID = 0,
                GuidItem = _taskbarIcon.TrayIcon.Id
            };

            return Shell_NotifyIconGetRect(ref identifier, out var bounds) == 0 &&
                   cursor.X >= bounds.Left && cursor.X < bounds.Right &&
                   cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern int Shell_NotifyIconGetRect(
            ref NotifyIconIdentifier identifier,
            out NativeRect iconLocation);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NotifyIconIdentifier
        {
            public uint CbSize;
            public IntPtr HWnd;
            public uint UID;
            public Guid GuidItem;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        // Numeric overlay follows the selected used/remaining display mode;
        // severity and colour always continue to represent actual usage.
        private static Icon CreateIcon(TraySeverity severity, double? displayPercent) =>
            TrayIconRenderer.CreateIcon(severity, displayPercent);

        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();

            var showItem = new MenuItem { Header = "Show Widget", FontWeight = FontWeights.SemiBold };
            showItem.Click += (_, _) => ShowWidget();

            var refreshItem = new MenuItem { Header = "Refresh Now" };
            refreshItem.Click += async (_, _) => await _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);

            var usageItem = new MenuItem { Header = "Usage stats" };
            usageItem.Click += (_, _) => ShowUsage();

            var settingsItem = new MenuItem { Header = "Settings..." };
            settingsItem.Click += (_, _) => ShowSettings();

            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();

            menu.Items.Add(showItem);
            menu.Items.Add(refreshItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(usageItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(exitItem);
            return menu;
        }

        /// <summary>
        /// Opens the usage dashboard, or focuses it when it is already open.
        /// </summary>
        public void ShowUsage()
        {
            _usageWindow.ShowUsage();
        }

        public void ShowSettings()
        {
            // Settings returns the user to wherever they came from: the widget
            // only comes back if it was on screen when settings was opened.
            _settingsWindow.ShowCentered(returnToWidgetOnDismiss: _widgetWindow.IsVisible);
        }

        public void HandleBackgroundUpdateResult(UpdateCheckResult result)
        {
            if (_settingsWindow.DataContext is SettingsViewModel settingsViewModel)
            {
                settingsViewModel.ApplyBackgroundUpdateResult(result);
            }

            if (result.Status != UpdateCheckStatus.UpdateAvailable ||
                result.Update is null ||
                result.FromCache)
            {
                return;
            }

            _taskbarIcon.ShowNotification(
                "AI Usage Tray update available",
                $"Version {result.Update.Version} is ready to review. Click to see what's new.",
                NotificationIcon.Info,
                customIconHandle: null,
                largeIcon: true,
                sound: false,
                respectQuietTime: true,
                realtime: false,
                timeout: TimeSpan.FromSeconds(8));
        }

        private void OnSettingsDismissing(object? sender, EventArgs e)
        {
            // Same path as a tray icon click, so position, theme and the silent
            // refresh all behave exactly as they normally do.
            ShowWidget();
        }

        private void OnOnboardingDismissing(object? sender, EventArgs e) => ShowWidget();

        /// <summary>Shows guided setup only for a fresh or interrupted first run.</summary>
        public void ShowInitialOnboardingIfNeeded()
        {
            if (!_settings.ShouldShowInitialOnboarding)
            {
                return;
            }

            var resume = string.Equals(
                _settings.OnboardingState,
                OnboardingStates.Started,
                StringComparison.OrdinalIgnoreCase);
            _onboardingWindow.ShowCentered(resume);
        }

        public void ShowWidget()
        {
            var wasVisible = _widgetWindow.IsVisible;

            if (!wasVisible)
            {
                PositionWidget();
                _viewModel.ResetToOverview();
                _widgetWindow.Show();
            }

            _widgetWindow.Activate();

            // Opening the widget is usually just a local UI action. Refresh all
            // providers only when the last scheduled/manual refresh is stale.
            if (!wasVisible)
            {
                _ = _pulseOrchestrator.RefreshIfStaleAsync(
                    TimeSpan.FromMinutes(Math.Max(1, _settings.RefreshMinutes)),
                    CancellationToken.None).ContinueWith(
                    t => Log.Warning(t.Exception!.GetBaseException(), "Stale widget refresh failed"),
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
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

            var primaryId = _settings.PrimaryAccountId;

            var accounts = state.Providers
                .Where(pair =>
                    TrayAccountFilter.IsVisible(pair.Key, _settings.HasZaiKey, _settings.CopilotEnabled) ||
                    // The primary account drives the icon, so it must never be
                    // missing from the list the tooltip is built from.
                    (!string.IsNullOrWhiteSpace(primaryId) &&
                     pair.Key.Equals(primaryId, StringComparison.OrdinalIgnoreCase)))
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

            var showRemainingPercentages = _settings.ShowRemainingPercentages;
            var showWeeklyBeforeSession = _settings.ShowWeeklyBeforeSession;
            var status = TrayStatusComposer.Compose(
                accounts,
                DateTimeOffset.UtcNow,
                showRemainingPercentages,
                showWeeklyBeforeSession);
            var orderedAccounts = accounts;

            // When a primary account is configured, its status drives the icon
            // (colour + number); the tooltip still lists every account, primary first.
            if (!string.IsNullOrWhiteSpace(primaryId) &&
                state.Providers.TryGetValue(primaryId, out var primaryReading))
            {
                var label = displayNames.TryGetValue(primaryId, out var primaryName) ? primaryName : primaryId;
                var primaryAccount = primaryReading.Usage is { } primaryUsage
                    ? AccountUsageStatus.FromUsagePulse(label, primaryUsage)
                    : new AccountUsageStatus(label, null, null, null, null);
                var primaryStatus = TrayStatusComposer.Compose(
                    [primaryAccount],
                    DateTimeOffset.UtcNow,
                    showRemainingPercentages,
                    showWeeklyBeforeSession);
                orderedAccounts = accounts
                    .OrderBy(a => a.Label.Equals(label, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ToArray();
                var ordered = TrayStatusComposer.Compose(
                    orderedAccounts,
                    DateTimeOffset.UtcNow,
                    showRemainingPercentages,
                    showWeeklyBeforeSession);
                status = new TrayStatus(primaryStatus.HighestUsedPercent, primaryStatus.Severity, ordered.Tooltip)
                {
                    FullTooltip = ordered.FullTooltip
                };
            }

            var rows = TrayStatusComposer.ComposeRows(
                orderedAccounts,
                DateTimeOffset.UtcNow,
                showRemainingPercentages,
                showWeeklyBeforeSession);
            var compactRows = TrayStatusComposer.ComposeCompactRows(
                orderedAccounts,
                DateTimeOffset.UtcNow,
                showRemainingPercentages,
                showWeeklyBeforeSession);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            dispatcher.BeginInvoke(() => ApplyTrayStatus(status, rows, compactRows));
        }

        private void RaiseFloatingPanel()
        {
            if (_settings.ShowFloatingStatusPanel)
            {
                _statusPanelWindow.BringToFront();
            }
        }

        public void OnError(Exception error)
        {
            Log.Warning(error, "Tray usage stream failed");
        }

        public void OnCompleted()
        {
        }

        private void RebuildTooltipRows(IReadOnlyList<TrayAccountRow> rows)
        {
            _tooltipPanel.Children.Clear();
            if (rows.Count == 0)
            {
                var empty = new System.Windows.Controls.TextBlock { FontSize = 12, Text = "No AI usage data available" };
                empty.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
                _tooltipPanel.Children.Add(empty);
                return;
            }

            foreach (var row in rows)
            {
                var line = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, 2)
                };

                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                            UsedColor(row.WorstUsedPercent)))
                };
                line.Children.Add(dot);

                var label = new System.Windows.Controls.TextBlock
                {
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Text = row.Label,
                    VerticalAlignment = VerticalAlignment.Center
                };
                label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
                line.Children.Add(label);

                var text = new System.Windows.Controls.TextBlock
                {
                    FontSize = 12,
                    Text = "  " + row.WindowsText,
                    VerticalAlignment = VerticalAlignment.Center
                };
                text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextMutedBrush");
                line.Children.Add(text);

                _tooltipPanel.Children.Add(line);
            }
        }

        // Same bands as the tray icon, in the same four vivid colours.
        private static string UsedColor(double? usedPercent) => usedPercent is { } used
            ? BandPalette.Vivid(UsageBands.Of(used))
            : "#9CA3AF";

        private void ApplyTrayStatus(
            TrayStatus status,
            IReadOnlyList<TrayAccountRow> rows,
            IReadOnlyList<TrayCompactRow> compactRows)
        {
            RebuildTooltipRows(rows);

            var showRemainingPercentages = _settings.ShowRemainingPercentages;
            var displayPercent = status.GetDisplayPercent(showRemainingPercentages);
            var previousDisplayPercent = _lastAppliedStatus?.GetDisplayPercent(_lastShowRemainingPercentages);

            // Only redraw the icon when the severity or percent actually changed,
            // so the tray isn't constantly invalidated every refresh.
            var severityChanged = _lastAppliedStatus is null
                || _lastAppliedStatus.Severity != status.Severity
                || Math.Abs((previousDisplayPercent ?? -1) - (displayPercent ?? -1)) >= 1;
            if (severityChanged)
            {
                var replacement = CreateIcon(status.Severity, displayPercent);
                var previous = _currentIcon;
                _currentIcon = replacement;
                _taskbarIcon.Icon = replacement;
                previous.Dispose();
            }
            _lastAppliedStatus = status;
            _lastShowRemainingPercentages = showRemainingPercentages;

            if (!_settings.ShowFloatingStatusPanel)
            {
                _statusPanelWindow.HidePanel();
            }
            else if (_statusPanelWindow.Update(compactRows))
            {
                PositionStatusPanel();
            }
        }

        public void Dispose()
        {
            _hoverHideTimer?.Stop();
            _hoverTooltipWindow?.Close();
            _pulseSubscription.Dispose();
            _widgetWindow.SizeChanged -= OnWidgetSizeChanged;
            _settingsWindow.Dismissing -= OnSettingsDismissing;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _taskbarIcon.Dispose();
            _currentIcon.Dispose();
            _widgetWindow.Close();
            _settingsWindow.Close();
            _statusPanelWindow.Close();
            _usageWindow.ForceClose();
        }

        private void OnWidgetSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_widgetWindow.IsVisible)
            {
                return;
            }

            // SizeToContent means the height changes whenever the account count
            // or the active view changes. Re-anchor after the layout pass has
            // settled so the bottom edge stays glued to the taskbar corner
            // instead of drifting down off the screen.
            _widgetWindow.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_widgetWindow.IsVisible)
                    {
                        PositionWidget();
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            if (_widgetWindow.IsVisible)
            {
                PositionWidget();
            }
            if (_statusPanelWindow.IsVisible)
            {
                _statusPanelWindow.Dispatcher.BeginInvoke(
                    new Action(PositionStatusPanel),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void PositionWidget()
        {
            // SizeToContent leaves Height as NaN until the first layout pass,
            // so fall back to the measured size when it is not set yet.
            var width = double.IsNaN(_widgetWindow.Width) ? _widgetWindow.ActualWidth : _widgetWindow.Width;
            var height = double.IsNaN(_widgetWindow.Height) ? _widgetWindow.ActualHeight : _widgetWindow.Height;
            var position = _taskbarPosition.GetWidgetPosition(width, height, 12);
            _widgetWindow.Left = position.X;
            _widgetWindow.Top = position.Y;
        }

        private void PositionStatusPanel()
        {
            if (!_statusPanelWindow.IsVisible)
            {
                return;
            }

            _statusPanelWindow.UpdateLayout();
            var position = _taskbarPosition.GetWidgetPosition(
                _statusPanelWindow.ActualWidth,
                _statusPanelWindow.ActualHeight,
                12);
            _statusPanelWindow.Left = position.X;
            _statusPanelWindow.Top = position.Y;
        }
    }
}
