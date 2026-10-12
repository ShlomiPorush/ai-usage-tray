using costats.App.Services.Diagnostics;
using costats.Application.Settings;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace costats.Core.Tests.Diagnostics;

/// <summary>
/// Live on/off, automatic switch-off and deletion of the debug log. The debug
/// folder is a temp folder; nothing here touches the real data folder.
/// </summary>
public sealed class DebugLoggingControllerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ai-usage-tray-debug-tests", Guid.NewGuid().ToString("N"), "logs", "debug");
    private readonly AppSettings _settings = new();
    private readonly RecordingSettingsStore _store = new();
    private readonly ManualTimeProvider _time = new(Start);
    private readonly LoggingLevelSwitch _levelSwitch = new(LogEventLevel.Information);
    private readonly DebugLogSink _sink;
    private readonly Logger _appLogger;

    public DebugLoggingControllerTests()
    {
        _sink = new DebugLogSink(_directory);
        // The same shape as the app logger: one level switch, the debug sink
        // permanently attached.
        _appLogger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_levelSwitch)
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        _sink.Close();
        _appLogger.Dispose();
        try
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_directory))!, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }

    private DebugLoggingController Controller() =>
        new(_settings, _store, _sink, _levelSwitch, LogEventLevel.Information, _time, _appLogger);

    private string ReadDebugLog()
    {
        var file = Assert.Single(Directory.GetFiles(_directory, "debug-*.log"));
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void While_off_nothing_is_written_and_no_folder_exists()
    {
        _appLogger.Debug("debug line while off");
        _appLogger.Information("information line while off");

        Assert.False(_sink.IsOpen);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task Turning_on_writes_debug_events_and_records_when()
    {
        using var controller = Controller();

        await controller.SetEnabledAsync(true);
        _appLogger.Debug("debug line while on");

        Assert.True(_settings.DebugLoggingEnabled);
        Assert.Equal(Start, _settings.DebugLoggingEnabledAt);
        Assert.Equal(Start.AddDays(7), controller.ExpiresAt);
        Assert.Equal(LogEventLevel.Debug, _levelSwitch.MinimumLevel);
        Assert.Contains("debug line while on", ReadDebugLog(), StringComparison.Ordinal);
        Assert.Equal(1, _store.Saves);
    }

    [Fact]
    public async Task Turning_off_releases_the_open_file_and_deletes_the_folder()
    {
        using var controller = Controller();
        await controller.SetEnabledAsync(true);
        _appLogger.Debug("held open");

        // The file sink holds its file open, so the folder cannot be deleted
        // while debug logging is on. This is the problem the sink design solves.
        Assert.ThrowsAny<IOException>(() => Directory.Delete(_directory, recursive: true));

        await controller.SetEnabledAsync(false);

        Assert.False(_sink.IsOpen);
        Assert.False(Directory.Exists(_directory));
        Assert.False(_settings.DebugLoggingEnabled);
        Assert.Null(_settings.DebugLoggingEnabledAt);
        Assert.Equal(LogEventLevel.Information, _levelSwitch.MinimumLevel);
        Assert.Equal(2, _store.Saves);

        _appLogger.Debug("after turning off");
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task Turning_on_again_starts_a_fresh_log()
    {
        using var controller = Controller();
        await controller.SetEnabledAsync(true);
        _appLogger.Debug("first session");
        await controller.SetEnabledAsync(false);

        await controller.SetEnabledAsync(true);
        _appLogger.Debug("second session");

        var text = ReadDebugLog();
        Assert.Contains("second session", text, StringComparison.Ordinal);
        Assert.DoesNotContain("first session", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switches_itself_off_after_seven_days_and_tells_the_settings_screen()
    {
        using var controller = Controller();
        var changes = 0;
        controller.StateChanged += (_, _) => changes++;
        await controller.SetEnabledAsync(true);
        _appLogger.Debug("day one");

        _time.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        await controller.CheckExpiryAsync();
        Assert.True(_settings.DebugLoggingEnabled);
        Assert.True(Directory.Exists(_directory));

        _time.Advance(TimeSpan.FromMinutes(1));
        await controller.CheckExpiryAsync();

        Assert.False(_settings.DebugLoggingEnabled);
        Assert.False(_sink.IsOpen);
        Assert.False(Directory.Exists(_directory));
        Assert.Equal(2, changes);
        Assert.Equal(2, _store.Saves);
    }

    [Fact]
    public async Task Startup_switches_off_an_expired_debug_log_and_deletes_it()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "debug-20260920.log"), "old");
        _settings.DebugLoggingEnabled = true;
        _settings.DebugLoggingEnabledAt = Start.AddDays(-8);
        using var controller = Controller();

        await controller.InitializeAsync();

        Assert.False(_settings.DebugLoggingEnabled);
        Assert.False(Directory.Exists(_directory));
        Assert.Equal(LogEventLevel.Information, _levelSwitch.MinimumLevel);
        Assert.Equal(1, _store.Saves);
    }

    [Fact]
    public async Task Startup_reopens_an_unexpired_debug_log()
    {
        _settings.DebugLoggingEnabled = true;
        _settings.DebugLoggingEnabledAt = Start.AddDays(-2);
        using var controller = Controller();

        await controller.InitializeAsync();
        _appLogger.Debug("after restart");

        Assert.True(_sink.IsOpen);
        Assert.Equal(LogEventLevel.Debug, _levelSwitch.MinimumLevel);
        Assert.Contains("after restart", ReadDebugLog(), StringComparison.Ordinal);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task Startup_retries_deleting_a_folder_a_previous_run_could_not_remove()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "debug-20260930.log"), "left behind");
        using var controller = Controller();

        await controller.InitializeAsync();

        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task A_file_held_by_another_program_is_left_for_the_next_start()
    {
        using var controller = Controller();
        await controller.SetEnabledAsync(true);
        var stray = Path.Combine(_directory, "opened-elsewhere.txt");
        await File.WriteAllTextAsync(stray, "x");

        using (new FileStream(stray, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await controller.SetEnabledAsync(false);
            Assert.False(_settings.DebugLoggingEnabled);
            Assert.True(Directory.Exists(_directory));
        }

        await controller.InitializeAsync();
        Assert.False(Directory.Exists(_directory));
    }

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public int Saves { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
