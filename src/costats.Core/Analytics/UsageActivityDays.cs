namespace costats.Core.Analytics;

/// <summary>
/// The first and last local day the logs hold usage on. Explains an empty
/// range: usage exists, just not in the days on screen.
/// </summary>
/// <param name="First">Earliest local day with usage.</param>
/// <param name="Last">Latest local day with usage.</param>
public readonly record struct UsageActivityDays(DateOnly First, DateOnly Last);
