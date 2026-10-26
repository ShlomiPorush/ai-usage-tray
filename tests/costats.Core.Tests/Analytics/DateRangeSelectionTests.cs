using costats.Core.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class DateRangeSelectionTests
{
    private static readonly DateOnly Oct1 = new(2026, 10, 1);

    private static DateRangeSelection Range(int maxSpan = 366, Func<DateOnly, bool>? allowed = null) =>
        new(false, maxSpan, allowed ?? (_ => true));

    [Fact]
    public void Two_plain_clicks_make_a_range_in_either_order()
    {
        var selection = Range();

        selection.Click(Oct1.AddDays(9));
        Assert.True(selection.IsAwaitingEnd);
        Assert.Equal(Oct1.AddDays(9), selection.From);
        Assert.Equal(Oct1.AddDays(9), selection.To);

        selection.Click(Oct1);
        Assert.False(selection.IsAwaitingEnd);
        Assert.Equal(Oct1, selection.From);
        Assert.Equal(Oct1.AddDays(9), selection.To);
    }

    [Fact]
    public void A_third_click_starts_over()
    {
        var selection = Range();
        selection.Click(Oct1);
        selection.Click(Oct1.AddDays(3));

        selection.Click(Oct1.AddDays(20));

        Assert.Equal(Oct1.AddDays(20), selection.From);
        Assert.Equal(Oct1.AddDays(20), selection.To);
        Assert.True(selection.IsAwaitingEnd);
    }

    [Fact]
    public void Clicking_the_same_day_twice_selects_that_day()
    {
        var selection = Range();
        selection.Click(Oct1);
        selection.Click(Oct1);

        Assert.Equal(Oct1, selection.From);
        Assert.Equal(Oct1, selection.To);
        Assert.False(selection.IsAwaitingEnd);
    }

    [Fact]
    public void Shift_click_closes_the_range_against_the_starting_selection()
    {
        var selection = new DateRangeSelection(false, 366, _ => true, Oct1, Oct1.AddDays(2));

        selection.Click(Oct1.AddDays(6), extend: true);

        Assert.Equal(Oct1, selection.From);
        Assert.Equal(Oct1.AddDays(6), selection.To);
    }

    [Fact]
    public void Days_that_are_not_allowed_change_nothing()
    {
        var selection = Range(allowed: day => day <= Oct1.AddDays(5));
        selection.Click(Oct1);

        Assert.False(selection.Click(Oct1.AddDays(6)));
        Assert.Equal(Oct1, selection.To);
        Assert.True(selection.IsAwaitingEnd);
    }

    [Fact]
    public void A_range_longer_than_the_limit_is_pulled_back_to_the_anchor()
    {
        var selection = Range(maxSpan: 7);
        selection.Click(Oct1);
        selection.Click(Oct1.AddDays(30));

        Assert.Equal(Oct1, selection.From);
        Assert.Equal(Oct1.AddDays(6), selection.To);
    }

    [Fact]
    public void Single_mode_picks_one_day_per_click()
    {
        var selection = new DateRangeSelection(true, 1, _ => true);
        selection.Click(Oct1);
        selection.Click(Oct1.AddDays(4));

        Assert.Equal(Oct1.AddDays(4), selection.From);
        Assert.Equal(Oct1.AddDays(4), selection.To);
        Assert.False(selection.IsAwaitingEnd);
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, 2026, 9, 27)]
    [InlineData(DayOfWeek.Monday, 2026, 9, 28)]
    public void The_month_grid_has_six_full_weeks_from_the_first_weekday(DayOfWeek firstDay, int year, int month, int day)
    {
        // October 2026 starts on a Thursday.
        var grid = DateRangeSelection.MonthGrid(2026, 10, firstDay);

        Assert.Equal(42, grid.Count);
        Assert.Equal(new DateOnly(year, month, day), grid[0]);
        Assert.Equal(firstDay, grid[0].DayOfWeek);
        Assert.Contains(Oct1, grid);
        Assert.Contains(new DateOnly(2026, 10, 31), grid);
    }
}
