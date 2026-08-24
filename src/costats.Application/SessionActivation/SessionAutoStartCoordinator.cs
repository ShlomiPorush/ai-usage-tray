using costats.Application.Abstractions;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using Microsoft.Extensions.Logging;

namespace costats.Application.SessionActivation;

/// <summary>
/// Starts a new provider session only after a previously observed five-hour
/// window has expired. Provider reads are refreshed immediately before the
/// prompt, so normal user activity always wins the race.
/// </summary>
public sealed class SessionAutoStartCoordinator
{
    public const int MaximumAttempts = 4; // initial attempt + three retries
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset IdleZaiWindowMarker = DateTimeOffset.UnixEpoch;

    private readonly IPulseOrchestrator _pulseOrchestrator;
    private readonly AppSettings _settings;
    private readonly ISessionWindowActivator _activator;
    private readonly ISessionActivationStateStore _stateStore;
    private readonly IClock _clock;
    private readonly ILogger<SessionAutoStartCoordinator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, SessionActivationCheckpoint>? _checkpoints;
    private bool _loadedStateIsReliable;

    public SessionAutoStartCoordinator(
        IPulseOrchestrator pulseOrchestrator,
        AppSettings settings,
        ISessionWindowActivator activator,
        ISessionActivationStateStore stateStore,
        IClock clock,
        ILogger<SessionAutoStartCoordinator> logger)
    {
        _pulseOrchestrator = pulseOrchestrator;
        _settings = settings;
        _activator = activator;
        _stateStore = stateStore;
        _clock = clock;
        _logger = logger;
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            var state = _pulseOrchestrator.CurrentState;
            if (state is null || state.IsRefreshing)
            {
                return;
            }

            // Snapshot the targets: a provider refresh publishes a replacement
            // PulseState while this check is running.
            var targets = EligibleTargets()
                .Where(target => state.Providers.ContainsKey(target.ProviderId))
                .ToList();

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CheckProviderAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckProviderAsync(
        SessionActivationTarget target,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (!TryReadReset(target.ProviderId, out var resetAt))
        {
            // Never infer expiry from an unavailable provider or from a bare
            // percentage. A real provider reset timestamp is required.
            return;
        }

        if (resetAt > now)
        {
            await ArmAsync(target.ProviderId, resetAt, cancellationToken).ConfigureAwait(false);
            _loadedStateIsReliable = true;
            return;
        }

        if (!_loadedStateIsReliable && !_checkpoints!.ContainsKey(target.ProviderId))
        {
            // A corrupt/unreadable state file might have lost a completed
            // marker. Wait until a genuinely new future window is observed;
            // sending now could duplicate a prompt after restart.
            return;
        }

        var checkpoint = GetOrCreateExpiredCheckpoint(target.ProviderId, resetAt, now);
        if (checkpoint.Completed || now < checkpoint.NextAttemptAt)
        {
            return;
        }

        // A user may have sent a prompt after our last global refresh. Refresh
        // this account immediately; if a new window exists, do not send ours.
        await _pulseOrchestrator
            .RefreshProviderAsync(target.ProviderId, cancellationToken)
            .ConfigureAwait(false);

        now = _clock.UtcNow;
        if (!TryReadReset(target.ProviderId, out var refreshedResetAt))
        {
            checkpoint.NextAttemptAt = now + RetryInterval;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (refreshedResetAt > now)
        {
            await ArmAsync(target.ProviderId, refreshedResetAt, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (refreshedResetAt != checkpoint.ObservedResetAt)
        {
            checkpoint = GetOrCreateExpiredCheckpoint(target.ProviderId, refreshedResetAt, now);
        }

        // Persist the attempt before the external side effect. If the app exits
        // after Claude Code sends the prompt but before we record success, the
        // next launch preflights the provider and sees the newly started window
        // instead of sending a duplicate.
        checkpoint.Attempts++;
        checkpoint.Completed = checkpoint.Attempts >= MaximumAttempts;
        checkpoint.NextAttemptAt = now + RetryInterval;
        await SaveAsync(cancellationToken).ConfigureAwait(false);

        var result = await _activator.ActivateAsync(target, cancellationToken).ConfigureAwait(false);
        var finishedAt = _clock.UtcNow;
        checkpoint.Succeeded = result.Succeeded;
        checkpoint.Completed = result.Succeeded || checkpoint.Attempts >= MaximumAttempts;
        checkpoint.NextAttemptAt = finishedAt + RetryInterval;
        await SaveAsync(cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            _logger.LogInformation(
                "Started a new five-hour window for {ProviderId} on attempt {Attempt}",
                target.ProviderId,
                checkpoint.Attempts);

            // Best effort: make the new countdown visible without waiting for
            // the ordinary polling interval. The successful prompt is still
            // considered authoritative if the quota endpoint updates slowly.
            await _pulseOrchestrator
                .RefreshProviderAsync(target.ProviderId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (checkpoint.Completed)
        {
            _logger.LogWarning(
                "Could not start a new five-hour window for {ProviderId} after {Attempts} attempts: {Error}",
                target.ProviderId,
                checkpoint.Attempts,
                result.Error ?? "unknown error");
        }
        else
        {
            _logger.LogWarning(
                "Five-hour window activation failed for {ProviderId}; retry {Retry} of 3 is due in five minutes: {Error}",
                target.ProviderId,
                checkpoint.Attempts,
                result.Error ?? "unknown error");
        }
    }

    private bool TryReadReset(string providerId, out DateTimeOffset resetAt)
    {
        resetAt = default;
        var state = _pulseOrchestrator.CurrentState;
        if (state is null || state.IsRefreshing ||
            !state.Providers.TryGetValue(providerId, out var reading) ||
            reading.Usage?.SessionWindow is not { } window ||
            window.Duration < TimeSpan.FromHours(4.5) ||
            window.Duration > TimeSpan.FromHours(5.5))
        {
            return false;
        }

        if (window.ResetsAt is { } providerReset)
        {
            resetAt = providerReset;
            return true;
        }

        // Z.AI removes nextResetTime after an unused five-hour bucket expires
        // and reports the idle state as 100% remaining (0 used). That exact
        // provider-specific shape is the signal that the next prompt must start
        // a new window. The fixed marker keeps retries and restart deduplication
        // stable until Z.AI publishes the next real reset timestamp.
        if (string.Equals(providerId, "zai", StringComparison.OrdinalIgnoreCase) &&
            reading.Usage.SessionUsed == 0 &&
            reading.Usage.SessionLimit is > 0)
        {
            resetAt = IdleZaiWindowMarker;
            return true;
        }

        return false;
    }

    private async Task ArmAsync(
        string providerId,
        DateTimeOffset resetAt,
        CancellationToken cancellationToken)
    {
        if (_checkpoints!.TryGetValue(providerId, out var current) &&
            current.ObservedResetAt == resetAt &&
            !current.Completed)
        {
            return;
        }

        _checkpoints[providerId] = new SessionActivationCheckpoint
        {
            ObservedResetAt = resetAt,
            Attempts = 0,
            NextAttemptAt = resetAt,
            Completed = false,
            Succeeded = false
        };
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private SessionActivationCheckpoint GetOrCreateExpiredCheckpoint(
        string providerId,
        DateTimeOffset resetAt,
        DateTimeOffset now)
    {
        if (_checkpoints!.TryGetValue(providerId, out var current) &&
            current.ObservedResetAt == resetAt)
        {
            return current;
        }

        var checkpoint = new SessionActivationCheckpoint
        {
            ObservedResetAt = resetAt,
            Attempts = 0,
            NextAttemptAt = now,
            Completed = false,
            Succeeded = false
        };
        _checkpoints[providerId] = checkpoint;
        return checkpoint;
    }

    private IEnumerable<SessionActivationTarget> EligibleTargets()
    {
        if (_settings.AutoStartClaudeFiveHourWindow)
        {
            foreach (var account in _settings.GetEffectiveAccounts().Where(account => account.IsClaude))
            {
                yield return new SessionActivationTarget(
                    $"claude:{account.Id}",
                    SessionActivationProvider.Claude,
                    account.ConfigDir);
            }
        }

        if (_settings.AutoStartZaiFiveHourWindow && _settings.HasZaiKey)
        {
            yield return new SessionActivationTarget("zai", SessionActivationProvider.Zai);
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_checkpoints is not null)
        {
            return;
        }

        var loaded = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _loadedStateIsReliable = loaded.IsReliable;
        _checkpoints = loaded.Checkpoints.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private Task SaveAsync(CancellationToken cancellationToken) =>
        _stateStore.SaveAsync(_checkpoints!, cancellationToken);
}
