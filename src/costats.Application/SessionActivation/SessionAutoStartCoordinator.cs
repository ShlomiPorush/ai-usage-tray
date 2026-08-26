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
    private static readonly DateTimeOffset IdleCodexWindowMarker = DateTimeOffset.UnixEpoch;

    private readonly IPulseOrchestrator _pulseOrchestrator;
    private readonly AppSettings _settings;
    private readonly ISessionWindowActivator _activator;
    private readonly ISessionActivationStateStore _stateStore;
    private readonly IClock _clock;
    private readonly ILogger<SessionAutoStartCoordinator> _logger;
    private readonly ISessionActivationWindowRegistry _windowRegistry;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, SessionActivationCheckpoint>? _checkpoints;
    private bool _loadedStateIsReliable;

    public SessionAutoStartCoordinator(
        IPulseOrchestrator pulseOrchestrator,
        AppSettings settings,
        ISessionWindowActivator activator,
        ISessionActivationStateStore stateStore,
        IClock clock,
        ILogger<SessionAutoStartCoordinator> logger,
        ISessionActivationWindowRegistry? windowRegistry = null)
    {
        _pulseOrchestrator = pulseOrchestrator;
        _settings = settings;
        _activator = activator;
        _stateStore = stateStore;
        _clock = clock;
        _logger = logger;
        _windowRegistry = windowRegistry ?? new SessionActivationWindowRegistry();
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
        if (!_loadedStateIsReliable && !_checkpoints!.ContainsKey(target.ProviderId))
        {
            // Persist a target-specific recovery barrier. If another provider
            // observes a healthy future window, this provider must still remain
            // closed until it independently does the same.
            _checkpoints[target.ProviderId] = new SessionActivationCheckpoint
            {
                RequiresFutureObservation = true
            };
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        var now = _clock.UtcNow;
        _checkpoints!.TryGetValue(target.ProviderId, out var checkpoint);
        if (!TryReadCurrentReading(target.ProviderId, out var reading) ||
            !TryResolveReset(target, reading, checkpoint, out var resetAt))
        {
            return;
        }

        if (resetAt > now)
        {
            var restoredVisibleCodexWindow =
                target.Provider == SessionActivationProvider.Codex &&
                checkpoint is { ObservedActiveWindow: true, Succeeded: true } &&
                _windowRegistry.Confirm(target.ProviderId, resetAt) &&
                reading.Usage?.SessionWindow?.ResetsAt is null;

            await ArmAsync(
                    target.ProviderId,
                    resetAt,
                    target.Provider != SessionActivationProvider.Codex ||
                    reading.Usage?.SessionWindow?.ResetsAt is not null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (restoredVisibleCodexWindow)
            {
                await _pulseOrchestrator
                    .RefreshProviderAsync(target.ProviderId, cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        if (checkpoint?.RequiresFutureObservation == true)
        {
            return;
        }

        checkpoint = GetOrCreateExpiredCheckpoint(target.ProviderId, resetAt, now);
        if (checkpoint.Completed || now < checkpoint.NextAttemptAt)
        {
            return;
        }

        // A user may have sent a prompt after our last global refresh. Refresh
        // this account immediately; if a new window exists, do not send ours.
        var refresh = await _pulseOrchestrator
            .RefreshProviderForVerificationAsync(target.ProviderId, cancellationToken)
            .ConfigureAwait(false);

        now = _clock.UtcNow;
        if (!refresh.Succeeded || refresh.Reading is null ||
            !TryResolveReset(target, refresh.Reading, checkpoint, out var refreshedResetAt))
        {
            checkpoint.NextAttemptAt = now + RetryInterval;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (refreshedResetAt > now)
        {
            await ArmAsync(
                    target.ProviderId,
                    refreshedResetAt,
                    target.Provider != SessionActivationProvider.Codex ||
                    refresh.Reading.Usage?.SessionWindow?.ResetsAt is not null,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (refreshedResetAt != checkpoint.ObservedResetAt)
        {
            checkpoint = GetOrCreateExpiredCheckpoint(target.ProviderId, refreshedResetAt, now);
        }

        var verifiedWindowDuration = refresh.Reading.Usage!.SessionWindow!.Duration;

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
        if (result.Succeeded && target.Provider == SessionActivationProvider.Codex)
        {
            // Codex reports an idle 0%-used bucket as a rolling now+5h reset,
            // so the provider cannot give us a stable timestamp for the tiny
            // activation prompt. A successful official CLI invocation is the
            // authoritative start event; arm its next deadline from that event.
            checkpoint.ObservedResetAt = finishedAt + verifiedWindowDuration;
            checkpoint.ObservedActiveWindow = true;
            checkpoint.Attempts = 0;
            checkpoint.NextAttemptAt = checkpoint.ObservedResetAt;
            checkpoint.Completed = false;
            _windowRegistry.Confirm(target.ProviderId, checkpoint.ObservedResetAt);
        }
        else
        {
            checkpoint.Completed = result.Succeeded || checkpoint.Attempts >= MaximumAttempts;
            checkpoint.NextAttemptAt = finishedAt + RetryInterval;
        }
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

    private bool TryReadCurrentReading(string providerId, out ProviderReading reading)
    {
        reading = default!;
        var state = _pulseOrchestrator.CurrentState;
        if (state is null || state.IsRefreshing ||
            !state.Providers.TryGetValue(providerId, out var currentReading))
        {
            return false;
        }

        reading = currentReading;
        return true;
    }

    private static bool TryResolveReset(
        SessionActivationTarget target,
        ProviderReading reading,
        SessionActivationCheckpoint? checkpoint,
        out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (reading.Usage?.SessionWindow is not { } window ||
            window.Duration < TimeSpan.FromHours(4.5) ||
            window.Duration > TimeSpan.FromHours(5.5))
        {
            return false;
        }

        if (target.Provider == SessionActivationProvider.Codex &&
            reading.Usage.SessionUsed == 0 &&
            checkpoint is { Succeeded: true, Completed: false } &&
            checkpoint.ObservedResetAt != default)
        {
            // After our own successful prompt, prefer the stable deadline we
            // armed over a stale or rolling 0%-used provider timestamp.
            resetAt = checkpoint.ObservedResetAt;
            return true;
        }

        if (window.ResetsAt is { } providerReset)
        {
            resetAt = providerReset;
            return true;
        }

        if (reading.Usage.SessionUsed != 0 || reading.Usage.SessionLimit is not > 0)
        {
            return false;
        }

        // Z.AI reports an unused idle bucket as 0 used with no reset timestamp.
        // Keep its fixed marker so retries and restart deduplication remain stable.
        if (target.Provider == SessionActivationProvider.Zai)
        {
            resetAt = IdleZaiWindowMarker;
            return true;
        }

        if (target.Provider == SessionActivationProvider.Codex)
        {
            if (checkpoint is { RequiresFutureObservation: false } &&
                checkpoint.ObservedResetAt != default &&
                (checkpoint.ObservedActiveWindow || checkpoint.Succeeded))
            {
                resetAt = checkpoint.ObservedResetAt;
                return true;
            }

            // The GPT/Codex checkbox is an explicit opt-in to consume a small
            // prompt immediately when the account is idle. Corrupt-state
            // recovery still blocks this path per provider in CheckProviderAsync.
            resetAt = IdleCodexWindowMarker;
            return true;
        }

        // Claude is eligible only after this exact account persisted a real
        // reset; its first-run null state must never consume quota.
        if (target.Provider == SessionActivationProvider.Claude &&
            checkpoint is { RequiresFutureObservation: false } &&
            checkpoint.ObservedResetAt != default)
        {
            resetAt = checkpoint.ObservedResetAt;
            return true;
        }

        return false;
    }

    private async Task ArmAsync(
        string providerId,
        DateTimeOffset resetAt,
        bool observedActiveWindow,
        CancellationToken cancellationToken)
    {
        if (_checkpoints!.TryGetValue(providerId, out var current) &&
            current.ObservedResetAt == resetAt &&
            (current.ObservedActiveWindow || !observedActiveWindow) &&
            !current.RequiresFutureObservation &&
            !current.Completed)
        {
            return;
        }

        _checkpoints[providerId] = new SessionActivationCheckpoint
        {
            ObservedResetAt = resetAt,
            ObservedActiveWindow = observedActiveWindow,
            RequiresFutureObservation = false,
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
            RequiresFutureObservation = false,
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

        if (_settings.AutoStartCodexFiveHourWindow)
        {
            foreach (var account in _settings.GetEffectiveAccounts().Where(account => account.IsCodex))
            {
                yield return new SessionActivationTarget(
                    $"codex:{account.Id}",
                    SessionActivationProvider.Codex,
                    account.ConfigDir);
            }
        }

        if (_settings.AutoStartZaiFiveHourWindow && _settings.HasZaiCodingKey)
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
