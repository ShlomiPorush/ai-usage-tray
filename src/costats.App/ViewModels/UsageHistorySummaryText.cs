using System.Globalization;
using costats.Infrastructure.Analytics;

namespace costats.App.ViewModels;

/// <summary>
/// The usage-history line in Settings. No WPF, so it is tested on its own.
/// </summary>
public static class UsageHistorySummaryText
{
    /// <summary>What the stored history holds, or that saving is paused.</summary>
    public static string Describe(UsageHistoryInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.SavingPaused)
        {
            return "Saving usage history is paused: deleting it could not finish. Close any other copy of AI Usage Tray " +
                "and make sure the history folder is not read-only; it finishes on its own after that.";
        }

        if (info.FirstBucket is not { } first)
        {
            return "No usage history saved yet. It is saved in the background a minute after the app starts, then every 6 hours.";
        }

        var since = first.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return $"Saved usage from {since}, {FormatBytes(info.Bytes)}.";
    }

    /// <summary>
    /// The line right after "Delete history". A delete that could not finish
    /// stays recorded and pauses saving, which the line then says at once.
    /// </summary>
    /// <param name="deleted">What the delete returned.</param>
    /// <param name="info">The history as it is after the delete.</param>
    public static string AfterDelete(bool deleted, UsageHistoryInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (deleted)
        {
            return "Usage history deleted. Usage from now on will be saved again.";
        }

        return info.SavingPaused
            ? Describe(info)
            : "Usage history could not be fully deleted right now. Try again in a moment.";
    }

    /// <summary>A file size for Settings, such as "1.5 MB".</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L => $"{bytes / (1024d * 1024d):0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} bytes"
    };
}
