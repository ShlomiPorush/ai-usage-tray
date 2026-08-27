namespace costats.Core.Pulse;

/// <summary>
/// Converts the canonical used percentage into the user's selected display mode.
/// Provider data remains normalized to used percentage internally.
/// </summary>
public static class UsageDisplay
{
    public static double Percent(double usedPercent, bool showRemainingPercentages)
    {
        var used = Math.Clamp(usedPercent, 0, 100);
        return showRemainingPercentages ? 100 - used : used;
    }

    public static double Progress(double usedPercent, bool showRemainingPercentages) =>
        Percent(usedPercent, showRemainingPercentages) / 100.0;
}
