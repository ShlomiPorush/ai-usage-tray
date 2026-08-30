using costats.Core.Pulse;

namespace costats.Core.Alerts;

/// <summary>
/// Emits one alert when an account enters the sign-in-required state. The
/// account rearms only after a later pulse reports that it recovered.
/// </summary>
public sealed class AuthenticationAlertTracker
{
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Observe(PulseState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var current = state.Providers
            .Where(pair => pair.Value.AuthenticationState == ProviderAuthenticationState.SignInRequired)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _active.RemoveWhere(providerId => !current.Contains(providerId));
        return current.Where(providerId => _active.Add(providerId)).ToArray();
    }
}
