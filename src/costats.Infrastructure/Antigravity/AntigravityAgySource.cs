using System.Globalization;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Antigravity;
using costats.Core.Pulse;
using costats.Infrastructure.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace costats.Infrastructure.Antigravity;

/// <summary>
/// Google Antigravity quota through the official <c>agy</c> CLI. On the first
/// refresh after the user's minimum interval, the runner starts agy briefly,
/// reads the quota from its statusline payload and closes it; every other
/// refresh returns the last result. Gemini buckets are the main windows, the
/// shared "Claude &amp; GPT" buckets are scoped rows. No token is read, and the
/// account is never reported as signed out: agy gives no reliable signal.
/// </summary>
public sealed class AntigravityAgySource : ISignalSource
{
    public const string ProviderId = "antigravity";

    private static readonly TimeSpan DefaultEnabledPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly AppSettings _settings;
    private readonly IAgyProbeRunner _runner;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _enabledPollInterval;
    private readonly object _gate = new();

    private ProviderReading? _lastAttemptReading;
    private long _generation;
    private Task<ProviderReading>? _inFlight;
    private CancellationTokenSource? _runningDisable;

    public AntigravityAgySource(
        AppSettings settings,
        IAgyProbeRunner runner,
        ILogger<AntigravityAgySource>? logger = null)
        : this(settings, runner, logger, TimeProvider.System, DefaultEnabledPollInterval)
    {
    }

    internal AntigravityAgySource(
        AppSettings settings,
        IAgyProbeRunner runner,
        ILogger? logger,
        TimeProvider time,
        TimeSpan enabledPollInterval)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _enabledPollInterval = enabledPollInterval;
    }

    public ProviderProfile Profile => ProviderCatalog.Antigravity;

    /// <summary>Bumped whenever the provider is seen disabled; results of older attempts are dropped.</summary>
    internal long Generation => Interlocked.Read(ref _generation);

    private bool IsEnabled => _settings.AntigravityEnabled;

    private TimeSpan Interval => TimeSpan.FromMinutes(_settings.AntigravityMinIntervalMinutes);

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsEnabled)
        {
            NoteDisabled();
            return DisabledReading();
        }

        if (_settings.AntigravityProbeSuppressed)
        {
            lock (_gate)
            {
                return _lastAttemptReading ?? StatusReading("Antigravity check skipped in preview");
            }
        }

        Task<ProviderReading> attempt;
        bool joined;
        lock (_gate)
        {
            joined = _inFlight is { IsCompleted: false };
            if (!joined)
            {
                _inFlight = RunAttemptAsync(Interlocked.Read(ref _generation), cancellationToken);
            }

            attempt = _inFlight!;
        }

        if (!joined)
        {
            return await attempt.ConfigureAwait(false);
        }

        // Single flight: a second refresh shares the running attempt and never starts its own.
        try
        {
            return await attempt.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return CurrentReading(null);
        }
    }

    private async Task<ProviderReading> RunAttemptAsync(long generation, CancellationToken callerToken)
    {
        // Yield first so the caller's lock is released before any work runs.
        await Task.Yield();

        using var disable = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, disable.Token);
        using var watcherStop = new CancellationTokenSource();
        lock (_gate)
        {
            _runningDisable = disable;
        }

        var watcher = WatchEnabledAsync(disable, watcherStop.Token);
        AgyProbeResult result;
        try
        {
            result = await _runner
                .RunAsync(new AgyProbeRequest(Interval), linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !callerToken.IsCancellationRequested)
        {
            _logger.LogDebug("Antigravity check ended unexpectedly ({ErrorKind})", ex.GetType().Name);
            result = new AgyProbeResult(
                ex is OperationCanceledException ? AgyProbeStatus.Cancelled : AgyProbeStatus.LaunchFailed);
        }
        finally
        {
            watcherStop.Cancel();
            await watcher.ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_runningDisable, disable))
                {
                    _runningDisable = null;
                }
            }
        }

        callerToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // The provider may have been turned off after the watcher's last
            // poll and before the result arrived: never cache that result.
            if (!IsEnabled)
            {
                NoteDisabled();
                _logger.LogDebug("Antigravity check result discarded: the provider is turned off");
                return DisabledReading();
            }

            if (generation != Interlocked.Read(ref _generation) || result.Status == AgyProbeStatus.Cancelled)
            {
                _logger.LogDebug("Antigravity check result discarded: the provider was turned off meanwhile");
                return IsEnabled ? CurrentReadingLocked(null) : DisabledReading();
            }

            switch (result.Status)
            {
                case AgyProbeStatus.Success when result.Quota is not null:
                    _lastAttemptReading = SuccessReading(result.Quota, result.CompletedAt ?? _time.GetUtcNow());
                    return _lastAttemptReading;

                case AgyProbeStatus.Waiting when _lastAttemptReading is null && result.Quota is not null:
                    // A fresh process with nothing of its own yet: show the
                    // stored quota, marked restored so it never feeds an alert.
                    // It is not kept as this process's reading.
                    return SuccessReading(result.Quota, result.CompletedAt ?? _time.GetUtcNow()) with { IsRestored = true };

                case AgyProbeStatus.Waiting:
                    return CurrentReadingLocked(result.NextAllowedAt);

                case AgyProbeStatus.Busy:
                    return CurrentReadingLocked(null);

                default:
                    // The failure count for the back-off is kept by the runner in the shared schedule.
                    _lastAttemptReading = StatusReading(FailureText(result.Status));
                    return _lastAttemptReading;
            }
        }
    }

    /// <summary>Cancels the running attempt as soon as the provider is turned off.</summary>
    private async Task WatchEnabledAsync(CancellationTokenSource disable, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(_enabledPollInterval, stop).ConfigureAwait(false);
                if (!IsEnabled)
                {
                    NoteDisabled();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void NoteDisabled()
    {
        CancellationTokenSource? running;
        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
            running = _runningDisable;
            _lastAttemptReading = null;
        }

        try
        {
            running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private ProviderReading CurrentReading(DateTimeOffset? nextAllowedAt)
    {
        lock (_gate)
        {
            return IsEnabled ? CurrentReadingLocked(nextAllowedAt) : DisabledReading();
        }
    }

    private ProviderReading CurrentReadingLocked(DateTimeOffset? nextAllowedAt) =>
        _lastAttemptReading ?? StatusReading(nextAllowedAt is { } next
            ? $"Waiting for the next Antigravity check ({FormatLocalTime(next)})"
            : "Waiting for the next Antigravity check");

    internal static string FailureText(AgyProbeStatus status) => status switch
    {
        AgyProbeStatus.AgyNotFound => "agy (Antigravity CLI) was not found. Install it, then run agy once to sign in.",
        AgyProbeStatus.LaunchFailed => "agy could not be started. The next check tries again.",
        AgyProbeStatus.StorageFailed => "The Antigravity check folder could not be used. The next check tries again.",
        AgyProbeStatus.NoQuota => "agy returned no quota. If agy is not signed in, run agy once.",
        AgyProbeStatus.UnsupportedFormat => "Gemini quota not reported",
        _ => "Antigravity check failed"
    };

    private ProviderReading SuccessReading(AgyQuotaReading quota, DateTimeOffset capturedAt) =>
        new(
            Usage: quota.ToUsagePulse(ProviderId, capturedAt),
            Identity: new IdentityCard(ProviderId, Profile.DisplayName, null, null, quota.PlanTier, "agy"),
            StatusSummary: $"Updated {FormatLocalTime(capturedAt)}",
            CapturedAt: capturedAt,
            Confidence: ReadingConfidence.High,
            Source: ReadingSource.Cli);

    private ProviderReading StatusReading(string status) =>
        new(
            Usage: null,
            Identity: new IdentityCard(ProviderId, Profile.DisplayName, null, null, null, "agy"),
            StatusSummary: status,
            CapturedAt: _time.GetUtcNow(),
            Confidence: ReadingConfidence.Low,
            Source: ReadingSource.Cli);

    private ProviderReading DisabledReading() => StatusReading("Antigravity disabled in Settings");

    private string FormatLocalTime(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, _time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);
}
