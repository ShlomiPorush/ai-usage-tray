namespace costats.Application.Settings;

/// <summary>Persisted first-run onboarding states.</summary>
public static class OnboardingStates
{
    public const string Started = "started";
    public const string Dismissed = "dismissed";
    public const string Completed = "completed";

    /// <summary>
    /// Resolves the persisted state when the onboarding window is dismissed.
    /// Kept here so application shutdown follows the same tested transition as
    /// an explicit close from the onboarding UI.
    /// </summary>
    public static string AfterDismissal(string? currentState) =>
        string.Equals(currentState, Completed, StringComparison.OrdinalIgnoreCase)
            ? Completed
            : Dismissed;

    /// <summary>
    /// A hidden onboarding singleton is closed as part of normal application
    /// shutdown. Only a visible window represents a user dismissal.
    /// </summary>
    public static bool ShouldPersistDismissal(bool isWindowVisible) => isWindowVisible;
}
