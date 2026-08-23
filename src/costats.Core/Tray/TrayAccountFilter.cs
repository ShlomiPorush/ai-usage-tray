namespace costats.Core.Tray;

/// <summary>
/// Decides which provider keys of a pulse state are shown as accounts on the
/// tray surfaces (icon, hover tooltip) and published to the remote view.
/// </summary>
/// <remarks>
/// Kept as a pure function so the tray and the remote uploader cannot drift
/// apart again: Copilot used to be published remotely while being invisible in
/// the tray tooltip, even when it drove the icon as the primary account.
/// </remarks>
public static class TrayAccountFilter
{
    /// <summary>
    /// True when <paramref name="providerId"/> should be listed as an account.
    /// Per-account keys are prefixed ("claude:", "codex:"); the single-instance
    /// providers use a bare key ("claude", "zai", "copilot").
    /// </summary>
    public static bool IsVisible(string providerId, bool hasZaiKey, bool copilotEnabled)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        return providerId.StartsWith("claude:", StringComparison.OrdinalIgnoreCase)
            || providerId.StartsWith("codex:", StringComparison.OrdinalIgnoreCase)
            || providerId.Equals("claude", StringComparison.OrdinalIgnoreCase)
            || (providerId.Equals("zai", StringComparison.OrdinalIgnoreCase) && hasZaiKey)
            || (providerId.StartsWith("copilot:", StringComparison.OrdinalIgnoreCase) && copilotEnabled)
            || (providerId.Equals("copilot", StringComparison.OrdinalIgnoreCase) && copilotEnabled);
    }
}
