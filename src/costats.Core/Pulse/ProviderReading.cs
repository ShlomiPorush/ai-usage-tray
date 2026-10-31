namespace costats.Core.Pulse;

public enum ProviderAuthenticationState
{
    Unknown,
    Authenticated,
    SignInRequired
}

public sealed record ProviderReading(
    UsagePulse? Usage,
    IdentityCard? Identity,
    string? StatusSummary,
    DateTimeOffset CapturedAt,
    ReadingConfidence Confidence,
    ReadingSource Source,
    ProviderAuthenticationState AuthenticationState = ProviderAuthenticationState.Unknown)
{
    /// <summary>
    /// The reading was read back from disk, not observed by this process. It
    /// is shown, but never becomes an alert baseline or observation.
    /// </summary>
    public bool IsRestored { get; init; }
}
