namespace costats.Application.SessionActivation;

public enum SessionActivationProvider
{
    Claude,
    Codex,
    Zai
}

public sealed record SessionActivationTarget(
    string ProviderId,
    SessionActivationProvider Provider,
    string? ConfigDirectory = null);

public sealed record SessionActivationResult(bool Succeeded, string? Error = null)
{
    public static SessionActivationResult Success() => new(true);

    public static SessionActivationResult Failure(string error) => new(false, error);
}

public interface ISessionWindowActivator
{
    Task<SessionActivationResult> ActivateAsync(
        SessionActivationTarget target,
        CancellationToken cancellationToken);
}

public sealed class SessionActivationCheckpoint
{
    public DateTimeOffset ObservedResetAt { get; set; }

    /// <summary>
    /// True when the deadline came from an authoritative active window, or
    /// from this app's own successful activation. Old Codex rolling
    /// placeholders do not qualify.
    /// </summary>
    public bool ObservedActiveWindow { get; set; }

    /// <summary>
    /// True when this provider's checkpoint was lost with an unreadable state file.
    /// The provider must publish and persist a future reset before activation resumes.
    /// </summary>
    public bool RequiresFutureObservation { get; set; }

    /// <summary>The initial attempt plus completed retries for this reset.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>True after success or after the retry budget is exhausted.</summary>
    public bool Completed { get; set; }

    public bool Succeeded { get; set; }
}

public interface ISessionActivationStateStore
{
    Task<SessionActivationLoadResult> LoadAsync(
        CancellationToken cancellationToken);

    Task SaveAsync(
        IReadOnlyDictionary<string, SessionActivationCheckpoint> checkpoints,
        CancellationToken cancellationToken);
}

public sealed record SessionActivationLoadResult(
    IReadOnlyDictionary<string, SessionActivationCheckpoint> Checkpoints,
    bool IsReliable);

/// <summary>
/// Shares Codex windows that this app successfully started with the usage
/// source. Codex can round a tiny activation prompt down to 0% used, so the
/// official endpoint alone cannot distinguish that real fixed deadline from
/// its rolling idle placeholder.
/// </summary>
public interface ISessionActivationWindowRegistry
{
    bool Confirm(string providerId, DateTimeOffset resetAt);

    bool TryGetActive(string providerId, DateTimeOffset now, out DateTimeOffset resetAt);
}

public sealed class SessionActivationWindowRegistry : ISessionActivationWindowRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _resets =
        new(StringComparer.OrdinalIgnoreCase);

    public bool Confirm(string providerId, DateTimeOffset resetAt)
    {
        var changed = !_resets.TryGetValue(providerId, out var existing) || existing != resetAt;
        _resets[providerId] = resetAt;
        return changed;
    }

    public bool TryGetActive(string providerId, DateTimeOffset now, out DateTimeOffset resetAt)
    {
        if (_resets.TryGetValue(providerId, out resetAt) && resetAt > now)
        {
            return true;
        }

        _resets.TryRemove(providerId, out _);
        resetAt = default;
        return false;
    }
}
