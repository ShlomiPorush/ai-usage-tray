namespace costats.Core.Pulse;

/// <summary>
/// Converts the provider's canonical used percentage into the user's chosen
/// display value. Risk bands and progress calculations always stay based on
/// used percentage.
/// </summary>
public static class UsagePercentageDisplay
{
    public static double Value(double usedPercent, bool showLeft) =>
        showLeft
            ? 100 - Math.Clamp(usedPercent, 0, 100)
            : Math.Clamp(usedPercent, 0, 100);

    public static string Label(double usedPercent, bool showLeft) =>
        $"{(int)Math.Round(Value(usedPercent, showLeft))}% {(showLeft ? "left" : "used")}";

    public static string Compact(double usedPercent, bool showLeft) =>
        $"{(int)Math.Round(Value(usedPercent, showLeft))}%";
}
