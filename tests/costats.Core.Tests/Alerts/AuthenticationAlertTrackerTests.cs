using costats.Core.Alerts;
using costats.Core.Pulse;
using Xunit;

namespace costats.Core.Tests.Alerts;

public sealed class AuthenticationAlertTrackerTests
{
    [Fact]
    public void Observe_alerts_once_per_sign_in_failure_and_rearms_after_recovery()
    {
        var tracker = new AuthenticationAlertTracker();
        var failed = State(ProviderAuthenticationState.SignInRequired);
        var recovered = State(ProviderAuthenticationState.Authenticated);

        Assert.Equal(["codex:work"], tracker.Observe(failed));
        Assert.Empty(tracker.Observe(failed));
        Assert.Empty(tracker.Observe(recovered));
        Assert.Equal(["codex:work"], tracker.Observe(failed));
    }

    private static PulseState State(ProviderAuthenticationState authenticationState) =>
        new(
            new Dictionary<string, ProviderReading>
            {
                ["codex:work"] = new ProviderReading(
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    ReadingConfidence.High,
                    ReadingSource.Api,
                    authenticationState)
            },
            DateTimeOffset.UtcNow,
            [],
            false,
            RefreshTrigger.Scheduled);
}
