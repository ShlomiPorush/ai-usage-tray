using System.Globalization;
using costats.Infrastructure.Analytics;

namespace costats.App.ViewModels;

/// <summary>Kept free of WPF so the Settings text can be tested on its own.</summary>
public static class UsageHistorySummaryText
{
    public static string Describe(UsageHistoryInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.FirstBucket is not { } first)
        {
            return "No usage history saved yet. It is saved in the background a minute after the app starts, then every 6 hours.";
        }

        var since = first.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return $"Saved usage from {since}, {FormatBytes(info.Bytes)}.";
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L => $"{bytes / (1024d * 1024d):0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} bytes"
    };
}
