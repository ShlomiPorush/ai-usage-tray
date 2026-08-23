using System.IO;
using System.Reflection;
using System.Windows;
using costats.App.Services;
using costats.App.Services.Updates;
using costats.App.ViewModels;
using costats.Application.Abstractions;
using costats.Application.Pulse;
using costats.Application.Security;
using costats.Application.Settings;
using costats.Application.Shell;
using costats.Infrastructure.Analytics;
using costats.Infrastructure.Providers;
using costats.Infrastructure.Pulse;
using costats.Infrastructure.Security;
using costats.Infrastructure.Settings;
using costats.Infrastructure.Time;
using costats.Infrastructure.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace costats.App
{
    public partial class App : System.Windows.Application
    {
        /// <summary>Dispatcher exceptions tolerated inside <see cref="DispatcherFailureWindow"/>.</summary>
        private const int DispatcherFailureLimit = 5;

        private static readonly TimeSpan DispatcherFailureWindow = TimeSpan.FromSeconds(60);

        /// <summary>Timestamps of recent dispatcher exceptions; touched on the UI thread only.</summary>
        private readonly Queue<DateTimeOffset> _dispatcherFailures = new();

        private IHost? _host;
        private SingleInstanceCoordinator? _singleInstance;
        private StartupUpdateCoordinator? _updateCoordinator;
        private CancellationTokenSource? _updateLoopCts;

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);

            BootstrapEarlyLogger();
            RegisterExceptionHandlers();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Log.Information("costats starting (v{Version}, PID {Pid})", version, Environment.ProcessId);

            _singleInstance = new SingleInstanceCoordinator("costats");
            if (!_singleInstance.IsPrimary)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SingleInstanceCoordinator.SignalPrimaryAsync(
                            _singleInstance.PipeName,
                            ActivationMessage.ShowWidget,
                            TimeSpan.FromSeconds(2));
                    }
                    catch
                    {
                        // Ignore activation errors on secondary instances.
                    }
                    finally
                    {
                        Dispatcher.Invoke(() => Shutdown(0));
                    }
                });
                return;
            }

            _ = InitializeAsync();
        }

        protected override async void OnExit(System.Windows.ExitEventArgs e)
        {
            Log.Information("Application exiting (ExitCode={ExitCode})", e.ApplicationExitCode);
            _updateLoopCts?.Cancel();
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

                // Remote view ships with a working endpoint, so the user only has
                // to tick the checkbox. These are runtime-only defaults: they are
                // never written back to settings.json, which lets a later release
                // change the service.
                settings.DefaultRemoteViewUploadUrl = ReadConfiguredUrl(startupConfiguration, "Costats:RemoteView:UploadUrl");
                settings.DefaultRemoteViewPageUrl = ReadConfiguredUrl(startupConfiguration, "Costats:RemoteView:PageUrl");

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
                    MaybeCaptureScreenshot(initializedTray);
                    return initializedTray;
                });

                if (_updateCoordinator is not null)
                {
                    _updateLoopCts = new CancellationTokenSource();
                    LogFireAndForget(
                        RunBackgroundUpdateChecksAsync(tray, _updateLoopCts.Token),
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

        private async Task RunBackgroundUpdateChecksAsync(TrayHost tray, CancellationToken cancellationToken)
        {
            if (_updateCoordinator is null)
            {
                return;
            }

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

                    try
                    {
                        var result = await _updateCoordinator.CheckForUpdateAsync(timeoutCts.Token).ConfigureAwait(false);
                        await Dispatcher.InvokeAsync(() => tray.HandleBackgroundUpdateResult(result));
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        Log.Warning("Background update check timed out");
                    }

                    await Task.Delay(_updateCoordinator.CheckInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal application shutdown.
            }
        }

        /// <summary>
        /// Dev/docs helper: "--screenshot &lt;path&gt;" opens the widget, waits for
        /// the first refresh, renders it to a PNG and exits.
        /// </summary>
        private void MaybeCaptureScreenshot(TrayHost tray)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "--screenshot");
            if (index < 0 || index + 1 >= args.Length)
            {
                return;
            }

            var path = args[index + 1];
            _ = Dispatcher.InvokeAsync(async () =>
            {
                tray.ShowWidget();
                await Task.Delay(TimeSpan.FromSeconds(15));
                try
                {
                    var window = _host!.Services.GetRequiredService<GlassWidgetWindow>();
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
                Shutdown(0);
            });
        }

        /// <summary>Reads a URL from configuration, treating blank as "not shipped".</summary>
        private static string? ReadConfiguredUrl(IConfiguration configuration, string key)
        {
            var value = configuration[key];
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
        private static void BootstrapEarlyLogger()
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "costats", "logs");
            Directory.CreateDirectory(logDir);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Debug()
                .WriteTo.File(
                    Path.Combine(logDir, "costats-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .Enrich.FromLogContext()
                .CreateLogger();
        }

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
                    loggerConfig
                        .ReadFrom.Configuration(context.Configuration)
                        .Enrich.FromLogContext();
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ISettingsStore>(settingsStore);
                    services.AddSingleton(settings);

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
                    services.AddSingleton<ICodexAppServerClient, CodexAppServerClient>();

                    // Per-account sources live in a registry so Settings edits
                    // apply at the next refresh without restarting the app.
                    services.AddSingleton<IAccountSourceRegistry, AccountSourceRegistry>();

                    services.AddSingleton<ISignalSource, CopilotPersonalSource>();
                    // Keep multicc discovery available for legacy settings compatibility.
                    services.AddSingleton<MulticcConfigReader>();
                    var tempReader = new MulticcConfigReader(
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<MulticcConfigReader>.Instance);
                    services.AddSingleton<IMulticcDiscovery>(
                        new MulticcDiscoveryService(tempReader, settings.MulticcConfigPath));

                    // Z.AI / GLM coding-plan monitor (Bearer-token auth).
                    services.AddSingleton<ZaiUsageFetcher>();
                    services.AddSingleton<IZaiUsageClient>(sp => sp.GetRequiredService<ZaiUsageFetcher>());
                    services.AddSingleton<IZaiModelUsageClient>(sp => sp.GetRequiredService<ZaiUsageFetcher>());
                    services.AddSingleton<ISignalSource>(sp => new ZaiUsageSource(
                        sp.GetRequiredService<IZaiUsageClient>(),
                        () => settings.ZAiCodingApiKey,
                        () => settings.ZAiApiKey,
                        () => settings.ZAiDisplayName));
                    services.AddSingleton<IPulseSnapshotWriter, JsonPulseSnapshotWriter>();
                    services.AddSingleton<IPulseOrchestrator, PulseOrchestrator>();
                    services.AddHostedService(sp => (PulseOrchestrator)sp.GetRequiredService<IPulseOrchestrator>());

                    // Local usage analytics: reads the agent logs on demand and
                    // computes token totals and raw API-rate cost. Nothing polls
                    // it yet; it stays idle until a caller asks for a report.
                    services.AddSingleton<IUsageAnalyticsService>(sp => new UsageAnalyticsService(
                        sp.GetRequiredService<AppSettings>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UsageAnalyticsService>>()));

                    services.AddSingleton<ICredentialVault, CredentialVault>();
                    services.AddSingleton<IGlassBackdropService, GlassBackdropService>();

                    services.AddSingleton<PulseViewModel>();
                    services.AddSingleton<SettingsViewModel>();
                    services.AddSingleton<UsageWindowViewModel>();
                    services.AddSingleton<GlassWidgetWindow>();
                    services.AddSingleton<SettingsWindow>();
                    services.AddSingleton<UsageWindow>();
                    services.AddSingleton<TrayStatusPanelWindow>();
                    services.AddSingleton<TaskbarPositionService>();
                    services.AddSingleton<TrayHost>();
                    services.AddSingleton<HotkeyService>();
                    services.AddSingleton<RemoteViewUploader>();
                })
                .Build();

            _host.Start();

            var lifetime = _host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Register(() => Log.Warning("Host is stopping"));

            _ = _host.Services.GetRequiredService<HotkeyService>();
            // Resolved eagerly: it subscribes to the pulse stream in its constructor.
            _ = _host.Services.GetRequiredService<RemoteViewUploader>();
            return _host.Services.GetRequiredService<TrayHost>();
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
