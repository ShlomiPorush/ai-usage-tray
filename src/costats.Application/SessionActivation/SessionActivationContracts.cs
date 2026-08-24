namespace costats.Application.SessionActivation;

public enum SessionActivationProvider
{
    Claude,
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
