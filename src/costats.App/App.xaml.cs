using System.IO;
using System.Reflection;
using System.Windows;
using costats.App.Services;
using costats.App.Services.Diagnostics;
using costats.App.Services.Updates;
using costats.App.ViewModels;
using costats.Application.Abstractions;
using costats.Application.Pulse;
using costats.Application.Alerts;
using costats.Application.Security;
using costats.Application.SessionActivation;
using costats.Application.Settings;
using costats.Application.Storage;
using costats.Infrastructure.Analytics;
using costats.Infrastructure.Antigravity;
using costats.Infrastructure.Storage;
using costats.Infrastructure.Providers;
using costats.Infrastructure.Pulse;
using costats.Infrastructure.Security;
using costats.Infrastructure.SessionActivation;
using costats.Infrastructure.Settings;
using costats.Infrastructure.Alerts;
using costats.Infrastructure.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace costats.App
{
    public partial class App : System.Windows.Application
    {
        /// <summary>Dispatcher exceptions tolerated inside <see cref="DispatcherFailureWindow"/>.</summary>
        private const int DispatcherFailureLimit = 5;

        private static readonly TimeSpan DispatcherFailureWindow = TimeSpan.FromSeconds(60);
        private static readonly string[] ScreenshotFlags =
        [
            "--screenshot",
            "--settings-screenshot",
            "--usage-screenshot",
            "--onboarding-screenshot",
            "--onboarding-fallback-screenshot"
        ];

        /// <summary>Timestamps of recent dispatcher exceptions; touched on the UI thread only.</summary>
        private readonly Queue<DateTimeOffset> _dispatcherFailures = new();

        private IHost? _host;
        private SingleInstanceCoordinator? _singleInstance;
        private StartupUpdateCoordinator? _updateCoordinator;
        private CancellationTokenSource? _updateLoopCts;

        /// <summary>
        /// Drives the minimum level of the app logger: the configured level
        /// normally, Debug while debug logging is on. The normal log file keeps
        /// the configured level either way.
        /// </summary>
        private readonly LoggingLevelSwitch _logLevelSwitch = new(LogEventLevel.Information);
        private readonly DebugLogSink _debugLogSink = new(AppDataPaths.DebugLogsDirectory);
        private LogEventLevel _baseLogLevel = LogEventLevel.Information;
        private DebugLoggingController? _debugLogging;

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);

            RegisterExceptionHandlers();

            // The mutex/pipe name stays "costats" on purpose: it is how this
            // build recognises an older running instance and hands off to it
            // instead of moving that instance's data folder out from under it.
            _singleInstance = new SingleInstanceCoordinator("costats");
            if (!_singleInstance.IsPrimary)
            {
                // Until this instance knows it is the primary it writes nothing
                // under the data folder: the running primary owns it, and the
                // one-time data folder move must not race a log file this
                // process created.
                BootstrapDebugOnlyLogger();
                Log.Information("Second instance starting (PID {Pid}); handing off to the primary", Environment.ProcessId);

                // We did not create the single-instance mutex. Usually that means a
                // genuine primary is already running, so we hand our activation to
                // it and exit. But the mutex name is only same-user scoped: it can
                // be squatted by another same-user process that is not this app, or
                // left held by a primary that died without releasing it. In those
                // cases nobody answers the activation pipe, so exiting would suppress
                // the tray silently. We therefore exit only when the hand-off is
                // accepted, and otherwise fall through and run as the primary.
                _ = Task.Run(async () =>
                {
                    var handedOff = await SingleInstanceCoordinator.TryHandoffToPrimaryAsync(
                        _singleInstance.PipeName,
                        ActivationMessage.ShowWidget,
                        TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (handedOff)
                        {
                            // A real primary received the activation; this instance is done.
                            Shutdown(0);
                        }
                        else
                        {
                            PrepareDataFolderAndLogging();
                            Log.Warning(
                                "Single-instance lock was held but no primary answered the activation pipe; starting anyway");
                            _ = InitializeAsync();
                        }
                    });
                });
                return;
            }

            PrepareDataFolderAndLogging();
            _ = InitializeAsync();
        }

        /// <summary>
        /// Runs in the primary instance only, before anything opens a file in
        /// the data folder: moves data written by 2.18 and earlier from %LOCALAPPDATA%\costats,
        /// then starts the file logger and records what the move did.
        /// </summary>
        private void PrepareDataFolderAndLogging()
        {
            LegacyDataMigrationResult? migration = null;
            Exception? migrationError = null;
            try
            {
                migration = LegacyDataMigrator.ForCurrentUser().Migrate();
            }
            catch (Exception ex)
            {
                // The move is best effort and retried on the next start; it
                // must never stop the app from starting.
                migrationError = ex;
            }

            BootstrapEarlyLogger(JsonSettingsStore.IsDebugLoggingOnAtStartup(DateTimeOffset.UtcNow));

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Log.Information("costats starting (v{Version}, PID {Pid})", version, Environment.ProcessId);
            LogMigration(migration, migrationError);
        }

        private static void LogMigration(LegacyDataMigrationResult? migration, Exception? error)
        {
            if (error is not null)
            {
                Log.Warning(error, "Moving data from {LegacyRoot} failed; it is retried on the next start", AppDataPaths.LegacyRoot);
                return;
            }

            if (migration is null || !migration.LegacyFolderFound)
            {
                return;
            }

            Log.Information(
                "Moved {Moved} item(s) from {LegacyRoot} to {DataRoot}; old folder {Outcome}",
                migration.Moved.Count,
                AppDataPaths.LegacyRoot,
                AppDataPaths.Root,
                migration.LegacyFolderRemoved ? "removed" : "kept");

            if (migration.Kept.Count > 0)
            {
                Log.Information(
                    "Left {Count} item(s) in the old data folder because the new folder already has them: {Items}",
                    migration.Kept.Count,
                    string.Join(", ", migration.Kept));
            }

            foreach (var failure in migration.Failed)
            {
                Log.Warning(
                    "Could not move {Item} from the old data folder; retrying on the next start: {Error}",
                    failure.RelativePath.Length == 0 ? "(folder)" : failure.RelativePath,
                    failure.Error);
            }
        }

        protected override async void OnExit(System.Windows.ExitEventArgs e)
        {
            Log.Information("Application exiting (ExitCode={ExitCode})", e.ApplicationExitCode);
            _updateLoopCts?.Cancel();
            // Synchronously, before the first await: WPF does not wait for the rest
            // of this method, so a queued reset-checkpoint save must finish here.
            (_host?.Services.GetService(typeof(TrayHost)) as TrayHost)?.FlushPendingSaves();
            try
            {
                if (_host is not null)
                {
                    await _host.StopAsync();
                    _host.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error during host shutdown");
            }

            _singleInstance?.Dispose();
            _debugLogging?.Dispose();
            _updateLoopCts?.Dispose();
            Log.CloseAndFlush();
            base.OnExit(e);
        }

        private async Task InitializeAsync()
        {
            try
            {
                var startupConfiguration = BuildStartupConfiguration();
                _updateCoordinator = new StartupUpdateCoordinator(UpdateOptions.FromConfiguration(startupConfiguration));
                if (await _updateCoordinator.TryApplyPendingUpdateAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    Log.Information("Pending update is being applied, shutting down for update");
                    await Dispatcher.InvokeAsync(() => Shutdown(0));
                    return;
                }

                var settingsStore = new JsonSettingsStore(new CredentialVault());
                var settings = await settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);

                // Before the host logger exists, so it starts at the right level
                // and an expired debug log is gone before anything writes to it.
                _baseLogLevel = ReadBaseLogLevel(startupConfiguration);
                _logLevelSwitch.MinimumLevel = _baseLogLevel;
                _debugLogging = new DebugLoggingController(
                    settings,
                    settingsStore,
                    _debugLogSink,
                    _logLevelSwitch,
                    _baseLogLevel);
                await _debugLogging.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                ApplyPreviewTheme(settings);
                ApplyPreviewSafety(settings);

                // Remote view ships with a working endpoint, so the user only has
                // to tick the checkbox. These are runtime-only defaults: they are
                // never written back to settings.json, which lets a later release
                // change the service.
                settings.DefaultRemoteViewUploadUrl = ReadConfiguredValue(startupConfiguration, "Costats:RemoteView:UploadUrl");
                settings.DefaultRemoteViewPageUrl = ReadConfiguredValue(startupConfiguration, "Costats:RemoteView:PageUrl");
                settings.DefaultRemoteViewSigningKey = ReadConfiguredValue(startupConfiguration, "Costats:RemoteView:SigningKey");

                // The ZIP install leaves no uninstall entry behind, so the app
                // writes its own on every start. Idempotent, HKCU only, and it
                // keeps DisplayVersion in step after a self-update.
                LogFireAndForget(Task.Run(UninstallRegistration.Refresh), "UninstallRegistration");

                var tray = await Dispatcher.InvokeAsync(() =>
                {
                    ThemeService.Apply(settings.Theme);
                    Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
                    {
                        if (args.Category == Microsoft.Win32.UserPreferenceCategory.General &&
                            string.Equals(settings.Theme, ThemeService.SystemTheme, StringComparison.OrdinalIgnoreCase))
                        {
                            Dispatcher.BeginInvoke(() => ThemeService.Apply(settings.Theme));
                        }
                    };
                    var initializedTray = InitializeHost(settingsStore, settings);
                    LogFireAndForget(StartListenerAsync(initializedTray), "SingleInstanceListener");
                    if (!MaybeCaptureScreenshot(initializedTray))
                    {
                        initializedTray.ShowInitialOnboardingIfNeeded();
                    }
                    return initializedTray;
                });

                if (_updateCoordinator is not null)
                {
                    _updateLoopCts = new CancellationTokenSource();
                    LogFireAndForget(
                        RunBackgroundUpdateChecksAsync(tray, settings, _updateLoopCts.Token),
                        "UpdateCheck");
                }

            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Startup failed");
                System.Windows.MessageBox.Show(
                    $"Startup error: {ex.Message}\n\n{ex.StackTrace}",
                    "costats Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        private async Task RunBackgroundUpdateChecksAsync(
            TrayHost tray,
            AppSettings settings,
            CancellationToken cancellationToken)
        {
            if (_updateCoordinator is null)
            {
                return;
            }

            try
            {
                var nextCheck = DateTimeOffset.UtcNow;
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!settings.AutomaticUpdateChecksEnabled)
                    {
                        // Enabling the toggle should schedule a check promptly,
                        // not after the old six-hour deadline.
                        nextCheck = DateTimeOffset.UtcNow;
                    }
                    else if (DateTimeOffset.UtcNow >= nextCheck)
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

                        try
                        {
                            Log.Debug("Background update check started");
                            var result = await _updateCoordinator.CheckForUpdateAsync(timeoutCts.Token).ConfigureAwait(false);
                            Log.Debug(
                                "Background update check finished: {Status}, version {Version}, from cache {FromCache}",
                                result.Status,
                                result.Update?.Version,
                                result.FromCache);
                            await Dispatcher.InvokeAsync(() => tray.HandleBackgroundUpdateResult(result));
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            Log.Warning("Background update check timed out");
                        }

                        nextCheck = DateTimeOffset.UtcNow + _updateCoordinator.CheckInterval;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal application shutdown.
            }
        }

        /// <summary>
        /// Dev/docs helpers render the normal widget, settings, the usage window,
        /// guided onboarding, or its compact widget fallback to a PNG and exit.
        /// Settings takes <c>--settings-category Name</c> (default Automation).
        /// The usage window takes <c>--usage-range 1|7|30|90</c>,
        /// <c>--usage-from yyyy-MM-dd</c> with an optional <c>--usage-to</c>,
        /// <c>--usage-breakdown model|time</c>, <c>--usage-metric cost|tokens</c>,
        /// <c>--usage-compare previous|yyyy-MM-dd</c> with an optional
        /// <c>--usage-compare-to yyyy-MM-dd</c>, and
        /// <c>--usage-sort Column[,Column]</c>. Preview state is
        /// in-memory only and never touches settings.json.
        /// </summary>
        private bool MaybeCaptureScreenshot(TrayHost tray)
        {
            var args = Environment.GetCommandLineArgs();
            var flag = ScreenshotFlags.FirstOrDefault(candidate => Array.IndexOf(args, candidate) >= 0);
            if (flag is null)
            {
                return false;
            }

            var index = Array.IndexOf(args, flag);
            if (index < 0 || index + 1 >= args.Length)
            {
                return false;
            }

            var path = args[index + 1];
            _ = Dispatcher.InvokeAsync(async () =>
            {
                Window window;
                var wait = TimeSpan.FromSeconds(1);
                if (flag == "--settings-screenshot")
                {
                    var settingsWindow = _host!.Services.GetRequiredService<SettingsWindow>();
                    settingsWindow.ShowCentered(
                        returnToWidgetOnDismiss: false,
                        initialCategory: Enum.TryParse<SettingsCategory>(
                            ReadOptionalArgument(args, "--settings-category"), ignoreCase: true, out var category)
                            ? category
                            : SettingsCategory.Automation);
                    window = settingsWindow;
                }
                else if (flag == "--usage-screenshot")
                {
                    var usageViewModel = _host!.Services.GetRequiredService<UsageWindowViewModel>();
                    if (int.TryParse(ReadOptionalArgument(args, "--usage-range"), out var rangeDays) &&
                        UsageWindowViewModel.RangeChoices.Contains(rangeDays))
                    {
                        usageViewModel.RangeDays = rangeDays;
                    }

                    if (TryReadDate(args, "--usage-from", out var customFrom))
                    {
                        usageViewModel.ApplyCustomRange(
                            customFrom,
                            TryReadDate(args, "--usage-to", out var customTo) ? customTo : customFrom);
                    }

                    if (string.Equals(ReadOptionalArgument(args, "--usage-breakdown"), "time", StringComparison.OrdinalIgnoreCase))
                    {
                        usageViewModel.BreakdownIndex = 1;
                    }

                    if (string.Equals(ReadOptionalArgument(args, "--usage-metric"), "tokens", StringComparison.OrdinalIgnoreCase))
                    {
                        usageViewModel.MetricIndex = 1;
                    }

                    // "previous" compares with the period before; dates compare
                    // with the picked days, of any length.
                    var compare = ReadOptionalArgument(args, "--usage-compare");
                    if (string.Equals(compare, "previous", StringComparison.OrdinalIgnoreCase))
                    {
                        usageViewModel.ApplyComparison(enabled: true);
                    }
                    else if (TryReadDate(args, "--usage-compare", out var compareFrom))
                    {
                        usageViewModel.ApplyComparison(
                            enabled: true,
                            compareFrom,
                            TryReadDate(args, "--usage-compare-to", out var compareTo) ? compareTo : compareFrom);
                    }

                    // A column name, optionally twice to reverse it: "Tokens" or "Tokens,Tokens".
                    foreach (var column in (ReadOptionalArgument(args, "--usage-sort") ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        usageViewModel.SortCommand.Execute(column);
                    }

                    var usageWindow = _host.Services.GetRequiredService<UsageWindow>();
                    usageWindow.ShowUsage();
                    window = usageWindow;
                    wait = TimeSpan.FromSeconds(15);
                }
                else if (flag == "--onboarding-screenshot")
                {
                    var onboarding = _host!.Services.GetRequiredService<OnboardingWindow>();
                    onboarding.ShowCentered(
                        resume: false,
                        previewOnly: true,
                        previewStep: ReadPreviewStep(args));
                    window = onboarding;
                }
                else
                {
                    if (flag == "--onboarding-fallback-screenshot")
                    {
                        var settings = _host!.Services.GetRequiredService<AppSettings>();
                        settings.OnboardingState = OnboardingStates.Dismissed;
                        _host.Services.GetRequiredService<PulseViewModel>().NotifyOnboardingStateChanged();
                    }

                    var previewUpdateVersion = ReadOptionalArgument(args, "--preview-update-version");
                    if (previewUpdateVersion is not null)
                    {
                        tray.HandleBackgroundUpdateResult(new UpdateCheckResult(
                            UpdateCheckStatus.UpdateAvailable,
                            new AvailableUpdate(
                                previewUpdateVersion,
                                "Preview release notes",
                                "https://github.com/ShlomiPorush/ai-usage-tray/releases",
                                "preview.zip",
                                "https://github.com/ShlomiPorush/ai-usage-tray/releases/download/preview/preview.zip",
                                null),
                            FromCache: true));
                    }

                    tray.ShowWidget();
                    window = _host!.Services.GetRequiredService<GlassWidgetWindow>();
                    wait = TimeSpan.FromSeconds(15);
                }

                await Task.Delay(wait);
                try
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)Math.Ceiling(window.ActualWidth * 2),
                        (int)Math.Ceiling(window.ActualHeight * 2),
                        192, 192,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = File.Create(path);
                    encoder.Save(stream);
                    Log.Information("Screenshot saved to {Path}", path);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Screenshot capture failed");
                }
                window.Hide();
                Shutdown(0);
            });
            return true;
        }

        private static void ApplyPreviewTheme(AppSettings settings)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "--preview-theme");
            if (index < 0 || index + 1 >= args.Length)
            {
                return;
            }

            var theme = args[index + 1];
            if (theme is ThemeService.LightTheme or ThemeService.DarkTheme)
            {
                settings.Theme = theme;
            }
        }

        private static void ApplyPreviewSafety(AppSettings settings)
        {
            var args = Environment.GetCommandLineArgs();
            if (!ScreenshotFlags.Any(flag => Array.IndexOf(args, flag) >= 0))
            {
                return;
            }

            settings.AutoStartClaudeFiveHourWindow = false;
            settings.AutoStartCodexFiveHourWindow = false;
            settings.AutoStartZaiFiveHourWindow = false;
            // Runtime only, never saved: a preview run must not start agy, and
            // must not turn the provider off in the user's settings either.
            settings.AntigravityProbeSuppressed = true;

            if (ReadOptionalArgument(args, "--preview-update-version") is not null)
            {
                settings.AutomaticUpdateChecksEnabled = false;
            }

            if (Array.IndexOf(args, "--settings-screenshot") >= 0)
            {
                settings.SessionActivationScheduleEnabled = true;
                settings.SessionActivationScheduleStartHour = 6;
                settings.SessionActivationScheduleEndHour = 18;
            }
        }

        private static int ReadPreviewStep(string[] args)
        {
            var index = Array.IndexOf(args, "--onboarding-step");
            return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var step)
                ? Math.Clamp(step, 1, 3)
                : 1;
        }

        private static string? ReadOptionalArgument(string[] args, string flag)
        {
            var index = Array.IndexOf(args, flag);
            if (index < 0 || index + 1 >= args.Length)
            {
                return null;
            }

            var value = args[index + 1].Trim();
            return value.Length == 0 ? null : value;
        }

        /// <summary>Reads a <c>yyyy-MM-dd</c> argument value.</summary>
        private static bool TryReadDate(string[] args, string flag, out DateOnly date) =>
            DateOnly.TryParseExact(
                ReadOptionalArgument(args, flag),
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out date);

        /// <summary>
        /// The configured minimum level (<c>Serilog:MinimumLevel</c>, either a
        /// plain value or its <c>Default</c>): Information when shipped, Debug in
        /// a Development environment.
        /// </summary>
        private static LogEventLevel ReadBaseLogLevel(IConfiguration configuration)
        {
            var value = configuration["Serilog:MinimumLevel"] ?? configuration["Serilog:MinimumLevel:Default"];
            return Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level)
                ? level
                : LogEventLevel.Information;
        }

        /// <summary>Reads a value from configuration, treating blank as "not shipped".</summary>
        private static string? ReadConfiguredValue(IConfiguration configuration, string key)
        {
            var value = configuration[key];
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>
        /// Reads <c>Costats:Pricing</c>. A missing URL uses the LiteLLM catalog;
        /// an empty or non-https one disables the download, so prices then come
        /// from the bundled snapshot and pricing.json only.
        /// </summary>
        private static PricingCatalogOptions ReadPricingCatalogOptions(IConfiguration configuration)
        {
            var section = configuration.GetSection("Costats:Pricing");
            var url = section["CatalogUrl"];
            var hours = int.TryParse(section["RefreshIntervalHours"], out var parsed) ? Math.Clamp(parsed, 1, 168) : 24;

            Uri? catalogUrl;
            if (url is null)
            {
                catalogUrl = new Uri(PricingCatalogOptions.DefaultCatalogUrl);
            }
            else
            {
                catalogUrl = Uri.TryCreate(url.Trim(), UriKind.Absolute, out var configured) && configured.Scheme == Uri.UriSchemeHttps
                    ? configured
                    : null;
            }

            return new PricingCatalogOptions
            {
                CatalogUrl = catalogUrl,
                RefreshInterval = TimeSpan.FromHours(hours)
            };
        }

        private static IConfiguration BuildStartupConfiguration()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

            if (IsDevelopmentEnvironment())
            {
                builder.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
            }

            return builder.Build();
        }

        /// <summary>
        /// Development overrides (Serilog debug logging, for example) must never
        /// reach a shipped build, so they load only when the environment says so.
        /// The host builder applies the same rule through its own convention.
        /// </summary>
        private static bool IsDevelopmentEnvironment()
        {
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            return string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Bootstraps Serilog before the host is built so that startup and
        /// exception-handler logs reach the file sink even if host init fails.
        /// The host builder replaces this logger with the fully-configured one.
        /// </summary>
        /// <summary>
        /// The logger used until the host starts. When settings.json already has
        /// debug logging on (and not expired), the debug sink opens here, so the
        /// debug log also covers startup and the data folder move. The normal
        /// log stays at Information either way.
        /// </summary>
        private void BootstrapEarlyLogger(bool debugLoggingOn)
        {
            Directory.CreateDirectory(AppDataPaths.LogsDirectory);

            if (debugLoggingOn)
            {
                try
                {
                    _debugLogSink.Open();
                    _logLevelSwitch.MinimumLevel = LogEventLevel.Debug;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The controller retries once settings are loaded.
                }
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(_logLevelSwitch)
                .WriteTo.Debug(restrictedToMinimumLevel: LogEventLevel.Information)
                .WriteTo.File(
                    MainLogFilePath,
                    restrictedToMinimumLevel: LogEventLevel.Information,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    outputTemplate: LogOutputTemplate)
                .WriteTo.Sink(_debugLogSink)
                .Enrich.FromLogContext()
                .CreateLogger();
        }

        /// <summary>
        /// Logger for a second instance that has not yet confirmed a primary is
        /// running: debugger output only, so it creates nothing on disk.
        /// </summary>
        private static void BootstrapDebugOnlyLogger()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Debug()
                .Enrich.FromLogContext()
                .CreateLogger();
        }

        private const string LogOutputTemplate =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

        /// <summary>The daily application log, %LOCALAPPDATA%\AIUsageTray\logs\costats-YYYYMMDD.log.</summary>
        private static string MainLogFilePath => Path.Combine(AppDataPaths.LogsDirectory, "costats-.log");

        private void RegisterExceptionHandlers()
        {
            // One bad click handler must not kill a tray app, so UI exceptions
            // stay suppressed. A dispatcher that keeps faulting is different:
            // with no main window it would leave a zombie icon behind, so a
            // burst of failures shuts the app down instead.
            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error(args.Exception, "Unhandled UI exception");
                args.Handled = true;

                var now = DateTimeOffset.UtcNow;
                _dispatcherFailures.Enqueue(now);
                while (_dispatcherFailures.Count > 0 && now - _dispatcherFailures.Peek() > DispatcherFailureWindow)
                {
                    _dispatcherFailures.Dequeue();
                }

                if (_dispatcherFailures.Count > DispatcherFailureLimit)
                {
                    Log.Fatal(
                        args.Exception,
                        "{Count} unhandled UI exceptions within {Seconds}s, shutting down",
                        _dispatcherFailures.Count,
                        DispatcherFailureWindow.TotalSeconds);
                    _dispatcherFailures.Clear();
                    Shutdown(2);
                }
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Error(args.Exception, "Unobserved task exception");
                args.SetObserved();
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    Log.Fatal(ex, "Unhandled domain exception (IsTerminating={IsTerminating})", args.IsTerminating);
                    Log.CloseAndFlush();
                }
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                Log.CloseAndFlush();
            };
        }

        /// <summary>
        /// Observes a fire-and-forget task so exceptions are logged instead
        /// of silently swallowed or deferred to the finalizer.
        /// </summary>
        private static void LogFireAndForget(Task task, string operationName)
        {
            task.ContinueWith(
                t => Log.Error(t.Exception!.GetBaseException(), "Background task {Operation} faulted", operationName),
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private TrayHost InitializeHost(ISettingsStore settingsStore, AppSettings settings)
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration(config =>
                {
                    // CreateDefaultBuilder already layers appsettings.{Environment}.json
                    // on top of this, so no Development file is added by hand.
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                })
                .UseSerilog((context, services, loggerConfig) =>
                {
                    // The file sink is configured here rather than in
                    // appsettings.json so its folder comes from AppDataPaths.
                    loggerConfig
                        .ReadFrom.Configuration(context.Configuration)
                        // After ReadFrom: the configuration reader sets a fixed
                        // minimum level, which would otherwise win.
                        .MinimumLevel.ControlledBy(_logLevelSwitch)
                        .WriteTo.File(
                            MainLogFilePath,
                            restrictedToMinimumLevel: _baseLogLevel,
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: 14,
                            outputTemplate: LogOutputTemplate)
                        .WriteTo.Sink(_debugLogSink)
                        .Enrich.FromLogContext();
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ISettingsStore>(settingsStore);
                    services.AddSingleton(settings);
                    if (_debugLogging is not null)
                    {
                        services.AddSingleton(_debugLogging);
                    }

                    if (_updateCoordinator is not null)
                    {
                        services.AddSingleton(_updateCoordinator);
                    }

                    services.AddOptions<PulseOptions>()
                        .Configure<AppSettings>((options, appSettings) =>
                        {
                            var minutes = Math.Max(1, appSettings.RefreshMinutes);
                            options.RefreshInterval = TimeSpan.FromMinutes(minutes);
                        });

                    services.AddSingleton<IClock, SystemClock>();

                    services.AddSingleton<PulseBroadcaster>();
                    services.AddSingleton<ISourceSelector, SourceSelector>();
                    services.AddSingleton<CopilotUsageFetcher>();
                    services.AddSingleton(sp => new CodexAppServerClient(
                        logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CodexAppServerClient>>()));
                    services.AddSingleton<ICodexAppServerClient>(sp => sp.GetRequiredService<CodexAppServerClient>());
                    services.AddSingleton<ICodexResetCreditClient>(sp => sp.GetRequiredService<CodexAppServerClient>());
                    services.AddSingleton<CodexResetCreditService>();
                    services.AddSingleton<IClaudeResetCreditClient, ClaudeResetCreditClient>();
                    services.AddSingleton<ClaudeResetService>();
                    services.AddSingleton<ISessionActivationWindowRegistry, SessionActivationWindowRegistry>();

                    // Per-account sources live in a registry so Settings edits
                    // apply at the next refresh without restarting the app.
                    services.AddSingleton<IAccountSourceRegistry>(sp => new AccountSourceRegistry(
                        sp.GetRequiredService<AppSettings>(),
                        sp.GetRequiredService<ICodexAppServerClient>(),
                        sp.GetRequiredService<ISessionActivationWindowRegistry>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));

                    services.AddSingleton<ISignalSource, CopilotPersonalSource>();
                    // Google Antigravity: starts the official agy CLI briefly, at most
                    // once per the user's minimum interval, and reads its statusline quota.
                    services.AddSingleton<IAgyProbeRunner>(sp => new AgyProbeRunner(
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AgyProbeRunner>>()));
                    services.AddSingleton<ISignalSource>(sp => new AntigravityAgySource(
                        settings,
                        sp.GetRequiredService<IAgyProbeRunner>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AntigravityAgySource>>()));
                    // Z.AI / GLM coding-plan monitor (Bearer-token auth).
                    services.AddSingleton<IZaiUsageClient>(sp => new ZaiUsageFetcher(
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ZaiUsageFetcher>>()));
                    services.AddSingleton<ISignalSource>(sp => new ZaiUsageSource(
                        sp.GetRequiredService<IZaiUsageClient>(),
                        () => settings.ZAiCodingApiKey,
                        () => settings.ZAiApiKey,
                        () => settings.ZAiDisplayName));
                    // Startup usage cache: the last good usage, shown while the first refresh runs.
                    services.AddSingleton<IUsageSnapshotStore>(_ => new JsonUsageSnapshotStore());
                    services.AddSingleton<UsageStartupCache>();
                    services.AddSingleton<IPulseOrchestrator, PulseOrchestrator>();
                    services.AddHostedService(sp => (PulseOrchestrator)sp.GetRequiredService<IPulseOrchestrator>());

                    services.AddSingleton<ISessionActivationStateStore, JsonSessionActivationStateStore>();
                    services.AddSingleton<IUsageResetStateStore, JsonUsageResetStateStore>();
                    services.AddSingleton<ISessionWindowActivator, CliSessionWindowActivator>();
                    services.AddSingleton<SessionAutoStartCoordinator>();
                    services.AddHostedService<SessionAutoStartWorker>();

                    // Local usage analytics: reads the agent logs on demand and
                    // computes token totals and raw API-rate cost. Nothing polls
                    // it yet; it stays idle until a caller asks for a report.
                    // Prices come from the bundled snapshot, a daily copy of the
                    // public LiteLLM catalog and the user's pricing.json.
                    services.AddSingleton(sp =>
                    {
                        var options = ReadPricingCatalogOptions(sp.GetRequiredService<IConfiguration>());
                        var updater = options.CatalogUrl is null
                            ? null
                            : new PricingCatalogUpdater(
                                options,
                                logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PricingCatalogUpdater>>());
                        return new ModelPricingProvider(updater);
                    });
                    services.AddSingleton<IUsageAnalyticsService>(sp =>
                    {
                        var settings = sp.GetRequiredService<AppSettings>();
                        var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UsageAnalyticsService>>();
                        return new UsageAnalyticsService(settings, sp.GetRequiredService<ModelPricingProvider>(), logger)
                        {
                            History = new UsageHistoryOptions(
                                new UsageHistoryStore(logger: logger),
                                settings.GetEffectiveAccounts,
                                () => settings.UsageHistoryRetentionDays)
                        };
                    });
                    services.AddHostedService<UsageHistorySyncWorker>();

                    services.AddSingleton<ICredentialVault, CredentialVault>();

                    services.AddSingleton<PulseViewModel>();
                    services.AddSingleton<OnboardingViewModel>();
                    services.AddSingleton<SettingsViewModel>();
                    services.AddSingleton<UsageWindowViewModel>();
                    services.AddSingleton<GlassWidgetWindow>();
                    services.AddSingleton<OnboardingWindow>();
                    services.AddSingleton<SettingsWindow>();
                    services.AddSingleton<IFloatingPanelSettings>(sp => sp.GetRequiredService<SettingsViewModel>());
                    services.AddSingleton<TrayStatusPanelWindow>();
                    services.AddSingleton<UsageWindow>();
                    services.AddSingleton<TaskbarPositionService>();
                    services.AddSingleton<TrayHost>();
                    services.AddSingleton<HotkeyService>();
                    services.AddSingleton<RemoteViewUploader>();
                })
                .Build();

            _host.Start();
            if (settingsStore is JsonSettingsStore jsonSettingsStore)
            {
                jsonSettingsStore.Logger = _host.Services
                    .GetRequiredService<Microsoft.Extensions.Logging.ILogger<JsonSettingsStore>>();
            }
            _debugLogging?.StartExpiryChecks(TimeSpan.FromHours(1));

            var lifetime = _host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Register(() => Log.Warning("Host is stopping"));

            var hotkey = _host.Services.GetRequiredService<HotkeyService>();
            hotkey.ToggleRequested += (_, _) =>
                _host.Services.GetRequiredService<TrayHost>().ToggleWidget();
            // Resolved eagerly: it subscribes to the pulse stream in its constructor.
            _ = _host.Services.GetRequiredService<RemoteViewUploader>();
            var trayHost = _host.Services.GetRequiredService<TrayHost>();

            // The tray, the widget and the uploader have all subscribed: show the
            // last known usage now (or a first refresh they may have missed).
            _host.Services.GetRequiredService<IPulseOrchestrator>().PublishStartupState();
            return trayHost;
        }

        private async Task StartListenerAsync(TrayHost tray)
        {
            if (_singleInstance is null)
            {
                return;
            }

            await _singleInstance.StartListenerAsync(async message =>
            {
                if (message == ActivationMessage.ShowWidget)
                {
                    await Dispatcher.InvokeAsync(() => tray.ShowWidget());
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
