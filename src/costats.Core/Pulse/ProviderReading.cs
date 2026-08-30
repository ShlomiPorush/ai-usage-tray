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
    ProviderAuthenticationState AuthenticationState = ProviderAuthenticationState.Unknown);
