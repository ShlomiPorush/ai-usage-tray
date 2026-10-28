namespace costats.Core.Remote;

/// <summary>
/// The IANA name the usage payload carries for the desktop's time zone. The
/// viewer formats labels with it, so a Windows id is never sent.
/// </summary>
public static class RemoteUsageTimeZone
{
    /// <summary>
    /// Finds the IANA id of <paramref name="zone"/>: the id itself when it is
    /// already one, otherwise the Windows to IANA mapping. False when neither
    /// works, in which case usage sharing pauses.
    /// </summary>
    public static bool TryGetIanaId(TimeZoneInfo? zone, out string ianaId)
    {
        ianaId = string.Empty;
        if (zone is null || string.IsNullOrWhiteSpace(zone.Id))
        {
            return false;
        }

        if (zone.HasIanaId)
        {
            ianaId = zone.Id;
            return true;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var converted) &&
            !string.IsNullOrWhiteSpace(converted))
        {
            ianaId = converted;
            return true;
        }

        return false;
    }
}
