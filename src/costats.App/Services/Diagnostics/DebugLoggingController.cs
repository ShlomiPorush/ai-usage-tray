using System.IO;
using costats.Application.Diagnostics;
using costats.Application.Settings;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace costats.App.Services.Diagnostics;

/// <summary>
/// Turns debug logging on and off live, without a restart, and switches it
/// off by itself once <see cref="DebugLoggingPolicy.Lifetime"/> has passed.
///
/// On: the debug sink opens its file and the logger's level switch drops to
/// Debug, so Debug events are produced at all. The normal log keeps its own
/// minimum level and is unaffected. Off: the level switch goes back, the sink
/// flushes and closes its file, and the whole <c>logs\debug</c> folder is
/// deleted. A folder that cannot be deleted (something else holds a file) is
/// deleted on the next start instead.
/// </summary>
public sealed class DebugLoggingController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore? _store;
    private readonly DebugLogSink _sink;
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly LogEventLevel _baseLevel;
    private readonly TimeProvider _time;
    private readonly Serilog.ILogger? _log;
    private readonly object _gate = new();
    private ITimer? _expiryTimer;

    public DebugLoggingController(
        AppSettings settings,
        ISettingsStore? store,
        DebugLogSink sink,
        LoggingLevelSwitch levelSwitch,
        LogEventLevel baseLevel,
        TimeProvider? time = null,
        Serilog.ILogger? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store;
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _levelSwitch = levelSwitch ?? throw new ArgumentNullException(nameof(levelSwitch));
        _baseLevel = baseLevel;
        _time = time ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>Raised after debug logging turned on or off, from any thread.</summary>
    public event EventHandler? StateChanged;

    public bool IsEnabled => _settings.DebugLoggingEnabled;

    /// <summary>The folder the debug log is written to.</summary>
    public string Directory => _sink.Directory;

    /// <summary>When debug logging turns itself off, or null while it is off.</summary>
    public DateTimeOffset? ExpiresAt =>
        _settings is { DebugLoggingEnabled: true, DebugLoggingEnabledAt: { } start }
            ? DebugLoggingPolicy.ExpiresAt(start)
            : null;

    // Resolved on every call: the static logger is replaced once the host starts.
    private Serilog.ILogger Logger => _log ?? Log.ForContext<DebugLoggingController>();

    /// <summary>
    /// Applies the saved state at startup: reopens an unexpired debug log,
    /// switches an expired one off, and removes a folder a previous run could
    /// not delete.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        bool changed;
        lock (_gate)
        {
            if (DebugLoggingPolicy.IsExpired(_settings.DebugLoggingEnabled, _settings.DebugLoggingEnabledAt, _time.GetUtcNow()))
            {
                TurnOffCore("its seven days are over");
                changed = true;
            }
            else if (_settings.DebugLoggingEnabled)
            {
                TurnOnCore(resumed: true);
                changed = false;
            }
            else
            {
                DeleteFolder();
                changed = false;
            }
        }

        if (changed)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Turns debug logging on or off at the user's request.</summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (enabled == _settings.DebugLoggingEnabled && enabled == _sink.IsOpen)
            {
                return;
            }

            if (enabled)
            {
                _settings.DebugLoggingEnabled = true;
                _settings.DebugLoggingEnabledAt = _time.GetUtcNow();
                if (!TurnOnCore(resumed: false))
                {
                    _settings.DebugLoggingEnabled = false;
                    _settings.DebugLoggingEnabledAt = null;
                }
            }
            else
            {
                TurnOffCore("turned off in Settings");
            }
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Switches debug logging off when its lifetime has passed.</summary>
    public async Task CheckExpiryAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!DebugLoggingPolicy.IsExpired(_settings.DebugLoggingEnabled, _settings.DebugLoggingEnabledAt, _time.GetUtcNow()))
            {
                return;
            }

            TurnOffCore("its seven days are over");
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Checks for expiry every <paramref name="interval"/> until disposed.</summary>
    public void StartExpiryChecks(TimeSpan interval)
    {
        lock (_gate)
        {
            _expiryTimer?.Dispose();
            _expiryTimer = _time.CreateTimer(
                _ => _ = CheckExpirySafelyAsync(),
                null,
                interval,
                interval);
        }
    }

    private async Task CheckExpirySafelyAsync()
    {
        try
        {
            await CheckExpiryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Debug logging expiry check failed");
        }
    }

    private bool TurnOnCore(bool resumed)
    {
        try
        {
            _sink.Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "Could not open the debug log in {Directory}", _sink.Directory);
            return false;
        }

        _levelSwitch.MinimumLevel = _baseLevel < LogEventLevel.Debug ? _baseLevel : LogEventLevel.Debug;
        Logger.Information(
            resumed
                ? "Debug logging is on; it turns off by itself at {ExpiresAt:u}"
                : "Debug logging turned on; it turns off by itself at {ExpiresAt:u}",
            ExpiresAt);
        return true;
    }

    private void TurnOffCore(string reason)
    {
        _settings.DebugLoggingEnabled = false;
        _settings.DebugLoggingEnabledAt = null;

        // Written while the sink is still open, so the debug log records why
        // it ended right up to the moment it is deleted.
        Logger.Information("Debug logging turned off ({Reason}); deleting the debug log", reason);
        _levelSwitch.MinimumLevel = _baseLevel;
        _sink.Close();
        DeleteFolder();
    }

    private void DeleteFolder()
    {
        if (TryDeleteDirectory(_sink.Directory, out var error))
        {
            return;
        }

        Logger.Warning(
            "Could not delete the debug log folder {Directory}; it is deleted on the next start: {Error}",
            _sink.Directory,
            error);
    }

    /// <summary>Deletes <paramref name="directory"/> and everything in it. A missing folder counts as deleted.</summary>
    public static bool TryDeleteDirectory(string directory, out string? error)
    {
        error = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.Delete(directory, recursive: true);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 3)
                {
                    error = ex.Message;
                    return false;
                }

                // A virus scanner or indexer may hold the file for a moment.
                Thread.Sleep(100);
            }
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            await _store.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Saving the debug logging setting failed");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _expiryTimer?.Dispose();
            _expiryTimer = null;
        }
    }
}
