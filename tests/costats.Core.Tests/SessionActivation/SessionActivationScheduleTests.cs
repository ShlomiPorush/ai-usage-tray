using costats.Application.SessionActivation;
using costats.Application.Settings;
using Xunit;

namespace costats.Core.Tests.SessionActivation;

public sealed class SessionActivationScheduleTests
{
    [Fact]
    public void Disabled_schedule_allows_start_at_any_hour()
    {
        var settings = new AppSettings
        {
            SessionActivationScheduleEnabled = false,
            SessionActivationScheduleStartHour = 6,
            SessionActivationScheduleEndHour = 18
        };

        Assert.True(SessionActivationSchedule.AllowsStart(
            settings,
            AtHour(2),
            TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(21, true)]
    [InlineData(2, true)]
    [InlineData(6, false)]
    [InlineData(20, false)]
    public void Overnight_schedule_wraps_across_midnight(int hour, bool expected)
    {
        var settings = new AppSettings
        {
            SessionActivationScheduleEnabled = true,
            SessionActivationScheduleStartHour = 21,
            SessionActivationScheduleEndHour = 6
        };

        Assert.Equal(expected, SessionActivationSchedule.AllowsStart(
            settings,
            AtHour(hour),
            TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(-1, 18)]
    [InlineData(6, 24)]
    [InlineData(6, 6)]
    public void Invalid_or_empty_schedule_fails_closed(int startHour, int endHour)
    {
        var settings = new AppSettings
        {
            SessionActivationScheduleEnabled = true,
            SessionActivationScheduleStartHour = startHour,
            SessionActivationScheduleEndHour = endHour
        };

        Assert.False(SessionActivationSchedule.AllowsStart(
            settings,
            AtHour(9),
            TimeZoneInfo.Utc));
    }

    [Fact]
    public void Schedule_uses_the_configured_local_time_zone()
    {
        var settings = new AppSettings
        {
            SessionActivationScheduleEnabled = true,
            SessionActivationScheduleStartHour = 6,
            SessionActivationScheduleEndHour = 18
        };
        var utcPlusThree = TimeZoneInfo.CreateCustomTimeZone(
            "test-utc-plus-three",
            TimeSpan.FromHours(3),
            "Test UTC+3",
            "Test UTC+3");

        Assert.True(SessionActivationSchedule.AllowsStart(
            settings,
            AtHour(3),
            utcPlusThree));
        Assert.False(SessionActivationSchedule.AllowsStart(
            settings,
            AtHour(2),
            utcPlusThree));
    }

    private static DateTimeOffset AtHour(int hour) =>
        new(2026, 8, 24, hour, 0, 0, TimeSpan.Zero);
}
