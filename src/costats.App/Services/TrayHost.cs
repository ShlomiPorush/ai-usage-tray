using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using costats.App.ViewModels;
using costats.App.Services.Updates;
using costats.Application.Pulse;
using costats.Application.Alerts;
using costats.Application.Settings;
using costats.Application.Windowing;
using costats.Core.Alerts;
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
        private string? _lastFloatingPanelPosition;
        private readonly UsageAlertTracker _usageAlertTracker = new();

        /// <summary>A live state has been shown; UI thread only.</summary>
        private bool _appliedLiveState;

        /// <summary>The last queued reset-checkpoint save; UI thread only.</summary>
        private Task _resetCheckpointSave = Task.CompletedTask;
        private readonly UsageResetAlertTracker _usageResetAlertTracker;
        private readonly IUsageResetStateStore _usageResetStateStore;
        private readonly AuthenticationAlertTracker _authenticationAlertTracker = new();
        private Action? _notificationClickAction;

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
            AppSettings settings,
            IUsageResetStateStore usageResetStateStore)
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
            _usageResetStateStore = usageResetStateStore;
            _usageResetAlertTracker = new UsageResetAlertTracker(usageResetStateStore.Load());

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
            _taskbarIcon.TrayBalloonTipClicked += (_, _) =>
                (_notificationClickAction ?? ShowWidget).Invoke();
            _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
            TrayIconPromotionService.PromoteInBackground();
            _pulseSubscription = pulseOrchestrator.PulseStream.Subscribe(this);

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _widgetWindow.SizeChanged += OnWidgetSizeChanged;
            _statusPanelWindow.SizeChanged += OnStatusPanelSizeChanged;
            _settingsWindow.Dismissing += OnSettingsDismissing;
            if (_settingsWindow.DataContext is SettingsViewModel settingsViewModel)
            {
                settingsViewModel.TestNotificationRequested += OnTestNotificationRequested;
            }
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

            if (!_hoverRetention.ObserveTrayMouseMove(
                    DateTimeOffset.UtcNow,
                    IsPointerOverTrayIcon()))
            {
                return;
            }

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

            var result = Shell_NotifyIconGetRect(ref identifier, out var bounds);
            if (result != 0)
            {
                Log.Debug("Tray hover test: icon rectangle unavailable (0x{Result:X8})", result);
                return false;
            }

            var taskbar = FindRightToLeftTaskbar(bounds);
            return TrayIconHitTest.IsPointerOverIcon(
                cursor.X, cursor.Y,
                bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
                taskbar?.Left, taskbar?.Right);
        }

        // Returns the bounds of the taskbar holding the icon when that taskbar
        // is laid out right to left, because the shell then reports the icon
        // rectangle mirrored across it.
        private static NativeRect? FindRightToLeftTaskbar(NativeRect icon)
        {
            var centerX = (icon.Left + icon.Right) / 2;
            var centerY = (icon.Top + icon.Bottom) / 2;
            foreach (var className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                var taskbar = IntPtr.Zero;
                while ((taskbar = FindWindowEx(IntPtr.Zero, taskbar, className, null)) != IntPtr.Zero)
                {
                    if (!GetWindowRect(taskbar, out var rect) ||
                        centerX < rect.Left || centerX >= rect.Right ||
                        centerY < rect.Top || centerY >= rect.Bottom)
                    {
                        continue;
                    }

                    return (GetWindowLong(taskbar, GwlExStyle) & WsExLayoutRtl) != 0 ? rect : null;
                }
            }

            return null;
        }

        private const int GwlExStyle = -20;
        private const int WsExLayoutRtl = 0x00400000;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);

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
            ShowSettings(initialCategory: null);
        }

        private void ShowSettings(SettingsCategory? initialCategory)
        {
            // Settings returns the user to wherever they came from: the widget
            // only comes back if it was on screen when settings was opened.
            _settingsWindow.ShowCentered(
                returnToWidgetOnDismiss: _widgetWindow.IsVisible,
                initialCategory: initialCategory);
        }

        public void HandleBackgroundUpdateResult(UpdateCheckResult result)
        {
            _viewModel.ApplyUpdateCheckResult(result);

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

            _notificationClickAction = () => ShowSettings(SettingsCategory.Updates);
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
                _viewModel.RefreshUpdateAvailability();
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
            // A refreshing state without providers has nothing to show; applying
            // it would blank the icon and rows that are already on screen.
            if (state.IsRefreshing && state.Providers.Count == 0)
            {
                return;
            }

            var displayNames = _staticSources
                .Concat(_accountSources.Current)
                .Select(source => source.Profile)
                .GroupBy(profile => profile.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().DisplayName, StringComparer.OrdinalIgnoreCase);

            var primaryId = _settings.PrimaryAccountId;

            var accountEntries = state.Providers
                .Where(pair =>
                    TrayAccountFilter.IsVisible(
                        pair.Key,
                        _settings.HasZaiKey,
                        _settings.CopilotEnabled,
                        _settings.AntigravityEnabled) ||
                    // The primary account drives the icon, so it must never be
                    // missing from the list the tooltip is built from.
                    (!string.IsNullOrWhiteSpace(primaryId) &&
                     pair.Key.Equals(primaryId, StringComparison.OrdinalIgnoreCase)))
                .Select(pair =>
                {
                    var label = displayNames.TryGetValue(pair.Key, out var displayName)
                        ? displayName
                        : pair.Key;
                    var account = pair.Value.Usage is { } usage
                        ? AccountUsageStatus.FromUsagePulse(label, usage) with
                        {
                            RequiresSignIn = pair.Value.AuthenticationState ==
                                ProviderAuthenticationState.SignInRequired
                        }
                        : new AccountUsageStatus(
                            label,
                            null,
                            null,
                            null,
                            null,
                            RequiresSignIn: pair.Value.AuthenticationState ==
                                ProviderAuthenticationState.SignInRequired);
                    return (ProviderId: pair.Key, Account: account);
                })
                .OrderBy(entry => entry.Account.Label.StartsWith("Claude", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(entry => entry.Account.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var showRemainingPercentages = _settings.ShowRemainingPercentages;
            var showWeeklyBeforeSession = _settings.ShowWeeklyBeforeSession;
            var usageAlertRules = _settings.UsageAlertsEnabled
                ? _settings.UsageAlertRules
                    .Where(rule => rule.Enabled)
                    .Select(rule => new UsageAlertRule(rule.ProviderId, rule.ThresholdPercent))
                    .ToArray()
                : [];
            var resetAlertProviders = _settings.UsageAlertsEnabled && _settings.UsageResetAlertsEnabled
                ? usageAlertRules.Select(rule => rule.ProviderId).ToArray()
                : [];
            var accounts = accountEntries.Select(entry => entry.Account).ToArray();
            var status = TrayStatusComposer.Compose(
                accounts,
                DateTimeOffset.UtcNow,
                showRemainingPercentages,
                showWeeklyBeforeSession);
            var orderedEntries = accountEntries;

            // When a primary account is configured, its status drives the icon
            // (colour + number); the tooltip still lists every account, primary first.
            if (!string.IsNullOrWhiteSpace(primaryId) &&
                state.Providers.TryGetValue(primaryId, out var primaryReading))
            {
                var label = displayNames.TryGetValue(primaryId, out var primaryName) ? primaryName : primaryId;
                var primaryAccount = primaryReading.Usage is { } primaryUsage
                    ? AccountUsageStatus.FromUsagePulse(label, primaryUsage) with
                    {
                        RequiresSignIn = primaryReading.AuthenticationState ==
                            ProviderAuthenticationState.SignInRequired
                    }
                    : new AccountUsageStatus(
                        label,
                        null,
                        null,
                        null,
                        null,
                        RequiresSignIn: primaryReading.AuthenticationState ==
                            ProviderAuthenticationState.SignInRequired);
                var primaryStatus = TrayStatusComposer.Compose(
                    [primaryAccount],
                    DateTimeOffset.UtcNow,
                    showRemainingPercentages,
                    showWeeklyBeforeSession);
                orderedEntries = accountEntries
                    .OrderBy(entry => entry.ProviderId.Equals(primaryId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ToArray();
                var orderedAccounts = orderedEntries.Select(entry => entry.Account).ToArray();
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

            var orderedAccountStatuses = orderedEntries.Select(entry => entry.Account).ToArray();
            var rows = TrayStatusComposer.ComposeRows(
                orderedAccountStatuses,
                DateTimeOffset.UtcNow,
                showRemainingPercentages,
                showWeeklyBeforeSession);
            var floatingRows = TrayStatusComposer.ComposeRows(
                orderedEntries
                    .Where(entry => _settings.IsFloatingPanelProviderVisible(entry.ProviderId))
                    .Select(entry => entry.Account),
                DateTimeOffset.UtcNow,
                showRemainingPercentages,
                showWeeklyBeforeSession);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            dispatcher.BeginInvoke(() =>
            {
                // Decided here, on the UI thread, in the order the states are
                // shown: the stored usage never replaces a live state.
                var isCached = state.Trigger == RefreshTrigger.Cached;
                if (isCached && _appliedLiveState)
                {
                    return;
                }

                if (!isCached && state.Providers.Count > 0)
                {
                    _appliedLiveState = true;
                }

                ApplyTrayStatus(status, rows, floatingRows);
                if (isCached)
                {
                    return;
                }

                // The alert trackers run here, one state at a time, so a state
                // published twice can never fire an alert twice. Readings read
                // back from disk are never an observation.
                var observed = RestoredUsage.WithoutRestoredReadings(state);
                var usageAlerts = _usageAlertTracker.Observe(observed, usageAlertRules);
                var resetAlerts = _usageResetAlertTracker.Observe(observed, resetAlertProviders);
                SaveResetCheckpoints(_usageResetAlertTracker.ExportCheckpoints());
                var signInAlerts = _authenticationAlertTracker.Observe(observed);
                LogAlerts(usageAlerts, resetAlerts, signInAlerts);
                ShowUsageAlerts(usageAlerts, displayNames, showRemainingPercentages);
                ShowUsageResetAlerts(resetAlerts, displayNames);
                ShowSignInRequiredAlerts(signInAlerts, displayNames);
            });
        }

        /// <summary>
        /// Saves the reset checkpoints off the UI thread, one save after another,
        /// so a slow disk never holds up the tray and an older save never lands
        /// after a newer one. Called on the UI thread only.
        /// </summary>
        private void SaveResetCheckpoints(IReadOnlyCollection<UsageResetCheckpoint> checkpoints)
        {
            _resetCheckpointSave = _resetCheckpointSave.ContinueWith(
                _ => _usageResetStateStore.Save(checkpoints),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        private static void LogAlerts(
            IReadOnlyList<UsageThresholdAlert> usageAlerts,
            IReadOnlyList<UsageResetAlert> resetAlerts,
            IReadOnlyList<string> signInAlerts)
        {
            foreach (var alert in usageAlerts)
            {
                Log.Debug(
                    "Usage alert fired for {ProviderId}: {Window} {Scope} at {UsedPercent}% used",
                    alert.ProviderId,
                    alert.WindowLabel,
                    alert.Scope,
                    alert.UsedPercent);
            }

            foreach (var alert in resetAlerts)
            {
                Log.Debug("Usage reset alert fired for {ProviderId}: {Window} {Scope}", alert.ProviderId, alert.WindowLabel, alert.Scope);
            }

            foreach (var providerId in signInAlerts)
            {
                Log.Debug("Sign-in required alert fired for {ProviderId}", providerId);
            }
        }

        private void ShowSignInRequiredAlerts(
            IReadOnlyList<string> providerIds,
            IReadOnlyDictionary<string, string> displayNames)
        {
            foreach (var providerId in providerIds)
            {
                var accountName = displayNames.TryGetValue(providerId, out var displayName)
                    ? displayName
                    : providerId;
                _notificationClickAction = () => ShowSettings(SettingsCategory.Accounts);
                _taskbarIcon.ShowNotification(
                    $"{accountName} sign-in required",
                    "The account session could not be refreshed. Click to open account settings and sign in again.",
                    NotificationIcon.Error,
                    customIconHandle: null,
                    largeIcon: true,
                    sound: true,
                    respectQuietTime: true,
                    realtime: false,
                    timeout: TimeSpan.FromSeconds(12));
            }
        }

        private void ShowUsageAlerts(
            IReadOnlyList<UsageThresholdAlert> alerts,
            IReadOnlyDictionary<string, string> displayNames,
            bool showRemainingPercentages)
        {
            foreach (var alert in alerts)
            {
                var accountName = displayNames.TryGetValue(alert.ProviderId, out var displayName)
                    ? displayName
                    : alert.ProviderId;
                var windowName = string.IsNullOrWhiteSpace(alert.Scope)
                    ? alert.WindowLabel
                    : $"{alert.WindowLabel} · {alert.Scope}";
                var displayPercent = (long)Math.Round(
                    UsageDisplay.Percent(alert.UsedPercent, showRemainingPercentages));
                var displayMode = showRemainingPercentages ? "remaining" : "used";

                _notificationClickAction = ShowWidget;
                _taskbarIcon.ShowNotification(
                    $"{accountName} usage alert",
                    $"{windowName} is at {displayPercent}% {displayMode}.",
                    NotificationIcon.Warning,
                    customIconHandle: null,
                    largeIcon: true,
                    sound: true,
                    respectQuietTime: true,
                    realtime: false,
                    timeout: TimeSpan.FromSeconds(10));
            }
        }

        private void ShowUsageResetAlerts(
            IReadOnlyList<UsageResetAlert> alerts,
            IReadOnlyDictionary<string, string> displayNames)
        {
            foreach (var alert in alerts)
            {
                var accountName = displayNames.TryGetValue(alert.ProviderId, out var displayName)
                    ? displayName
                    : alert.ProviderId;
                var windowName = string.IsNullOrWhiteSpace(alert.Scope)
                    ? alert.WindowLabel
                    : $"{alert.WindowLabel} · {alert.Scope}";

                _notificationClickAction = ShowWidget;
                _taskbarIcon.ShowNotification(
                    $"{accountName} usage reset",
                    $"{windowName} usage reset to 0%.",
                    NotificationIcon.Info,
                    customIconHandle: null,
                    largeIcon: true,
                    sound: true,
                    respectQuietTime: true,
                    realtime: false,
                    timeout: TimeSpan.FromSeconds(10));
            }
        }

        private void OnTestNotificationRequested(object? sender, EventArgs e)
        {
            _notificationClickAction = ShowWidget;
            _taskbarIcon.ShowNotification(
                "AI Usage Tray test notification",
                "Test successful. Weekly usage reset alerts will appear here.",
                NotificationIcon.Info,
                customIconHandle: null,
                largeIcon: true,
                sound: true,
                respectQuietTime: false,
                realtime: true,
                timeout: TimeSpan.FromSeconds(10));
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

        private void ApplyTrayStatus(
            TrayStatus status,
            IReadOnlyList<TrayAccountRow> rows,
            IReadOnlyList<TrayAccountRow> floatingRows)
        {
            TrayAccountRowsPresenter.Rebuild(_tooltipPanel, rows);

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

            var floatingPanelPosition = FloatingPanelPlacementCalculator.NormalizeSetting(
                _settings.FloatingPanelPosition);
            var floatingPanelPositionChanged = !string.Equals(
                _lastFloatingPanelPosition,
                floatingPanelPosition,
                StringComparison.Ordinal);
            _lastFloatingPanelPosition = floatingPanelPosition;

            if (!_settings.ShowFloatingStatusPanel)
            {
                _statusPanelWindow.HidePanel();
            }
            else
            {
                var newlyShown = _statusPanelWindow.Update(floatingRows);
                if (newlyShown || floatingPanelPositionChanged)
                {
                    _statusPanelWindow.ResetManualPosition();
                    PositionStatusPanel();
                }
            }
        }

        public void Dispose()
        {
            _hoverHideTimer?.Stop();
            _hoverTooltipWindow?.Close();
            _pulseSubscription.Dispose();
            _widgetWindow.SizeChanged -= OnWidgetSizeChanged;
            _statusPanelWindow.SizeChanged -= OnStatusPanelSizeChanged;
            _settingsWindow.Dismissing -= OnSettingsDismissing;
            if (_settingsWindow.DataContext is SettingsViewModel settingsViewModel)
            {
                settingsViewModel.TestNotificationRequested -= OnTestNotificationRequested;
            }
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
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

        private void OnStatusPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_statusPanelWindow.IsVisible || _statusPanelWindow.IsManuallyPositioned)
            {
                return;
            }

            _statusPanelWindow.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_statusPanelWindow.IsVisible && !_statusPanelWindow.IsManuallyPositioned)
                    {
                        PositionStatusPanel();
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) =>
            Log.Debug("Power mode changed: {Mode}", e.Mode);

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            Log.Debug(
                "Display settings changed (widget visible {WidgetVisible}, status panel visible {PanelVisible})",
                _widgetWindow.IsVisible,
                _statusPanelWindow.IsVisible);
            if (_widgetWindow.IsVisible)
            {
                PositionWidget();
            }
            if (_statusPanelWindow.IsVisible)
            {
                _statusPanelWindow.ResetManualPosition();
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
            var position = _taskbarPosition.GetFloatingPanelPosition(
                _statusPanelWindow.Left,
                _statusPanelWindow.Top,
                _statusPanelWindow.ActualWidth,
                _statusPanelWindow.ActualHeight,
                12,
                _settings.FloatingPanelPosition);
            _statusPanelWindow.Left = position.X;
            _statusPanelWindow.Top = position.Y;
        }
    }
}
