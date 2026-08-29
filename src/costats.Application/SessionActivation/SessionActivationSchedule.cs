using costats.Application.Settings;

namespace costats.Application.SessionActivation;

public static class SessionActivationSchedule
{
    public static bool AllowsStart(
        AppSettings settings,
        DateTimeOffset now,
        TimeZoneInfo localTimeZone)
    {
        if (!settings.SessionActivationScheduleEnabled)
        {
            return true;
        }

        var startHour = settings.SessionActivationScheduleStartHour;
        var endHour = settings.SessionActivationScheduleEndHour;
        if (!IsValidHour(startHour) || !IsValidHour(endHour) || startHour == endHour)
        {
            return false;
        }

        var localHour = TimeZoneInfo.ConvertTime(now, localTimeZone).Hour;
        return startHour < endHour
            ? localHour >= startHour && localHour < endHour
            : localHour >= startHour || localHour < endHour;
    }

    public static bool IsValidHour(int hour) => hour is >= 0 and <= 23;
}
