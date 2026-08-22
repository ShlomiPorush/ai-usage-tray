namespace costats.Core.Pulse;

/// <summary>
/// The used-percent band every surface colours by. Higher is worse.
/// </summary>
public enum UsageBand
{
    Green,
    Yellow,
    Orange,
    Red
}

/// <summary>
/// The single band rule for the whole app: the used number alone decides.
/// A provider's own rating (<see cref="QuotaSeverity"/>) is still carried in
/// the models and in the remote payload, but it never moves a band or a
/// status line here.
/// </summary>
public static class UsageBands
{
    public const double YellowFrom = 50;
    public const double OrangeFrom = 75;
    public const double RedFrom = 90;

    public static UsageBand Of(double usedPercent) => usedPercent switch
    {
        >= RedFrom => UsageBand.Red,
        >= OrangeFrom => UsageBand.Orange,
        >= YellowFrom => UsageBand.Yellow,
        _ => UsageBand.Green
    };

    /// <summary>Headline wording for a used percentage, on the same bands.</summary>
    public static string StatusText(double usedPercent) => Of(usedPercent) switch
    {
        UsageBand.Red => "At limit",
        UsageBand.Orange => "Near limit",
        UsageBand.Yellow => "Moderate",
        _ => "OK"
    };
}
