using System.Globalization;
using costats.Core.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class UsageNumberFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(-5, "0")]
    [InlineData(348, "348")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1.00K")]
    [InlineData(16_500, "16.5K")]
    [InlineData(948_000, "948K")]
    [InlineData(106_000_000, "106M")]
    [InlineData(18_200_000, "18.2M")]
    [InlineData(1_380_000, "1.38M")]
    [InlineData(7_900_000_000, "7.90B")]
    [InlineData(13_300_000_000, "13.3B")]
    [InlineData(2_080_000_000, "2.08B")]
    public void Tokens_keeps_three_significant_digits(long tokens, string expected)
    {
        Assert.Equal(expected, UsageNumberFormat.Tokens(tokens));
    }

    [Fact]
    public void Tokens_promotes_a_value_that_rounds_past_its_own_unit()
    {
        // 999,600 must not render as "1000K".
        Assert.Equal("1.00M", UsageNumberFormat.Tokens(999_600));
        Assert.Equal("1.00B", UsageNumberFormat.Tokens(999_600_000));
    }

    [Fact]
    public void Tokens_is_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal culture must not turn "13.3B" into "13,3B".
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("13.3B", UsageNumberFormat.Tokens(13_300_000_000));
            Assert.Equal("$69,668.14", UsageNumberFormat.Money(69_668.14m));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(0, "$0.00")]
    [InlineData(0.05, "$0.05")]
    [InlineData(1152.15, "$1,152.15")]
    [InlineData(69668.14, "$69,668.14")]
    public void Money_is_exact_to_the_cent(decimal amount, string expected)
    {
        Assert.Equal(expected, UsageNumberFormat.Money(amount));
    }

    [Fact]
    public void CostOrUnpriced_never_prices_unknown_models_at_zero()
    {
        // Every token came from a model with no rates: saying "$0.00" would
        // claim the traffic was free.
        Assert.Equal("unpriced", UsageNumberFormat.CostOrUnpriced(0m, 4_200_000));

        // Nothing was used at all, so zero is the truth.
        Assert.Equal("$0.00", UsageNumberFormat.CostOrUnpriced(0m, 0));

        // A mixed bucket keeps its priced cost; the figure is a floor.
        Assert.Equal("$12.34", UsageNumberFormat.CostOrUnpriced(12.34m, 900_000));
        Assert.Equal("$12.34", UsageNumberFormat.CostOrUnpriced(12.34m, 0));
    }

    [Fact]
    public void Axis_ticks_draw_a_bare_zero_baseline()
    {
        Assert.Equal("0", UsageNumberFormat.AxisCost(0m));
        Assert.Equal("$1,500.00", UsageNumberFormat.AxisCost(1500m));
        Assert.Equal("0", UsageNumberFormat.AxisTokens(0));
        Assert.Equal("1.50B", UsageNumberFormat.AxisTokens(1_500_000_000));
    }

    [Theory]
    [InlineData(9248.77, 10400.92, "88.9%")]
    [InlineData(1152.15, 10400.92, "11.1%")]
    [InlineData(0.04, 10400.92, "0.0%")]
    [InlineData(1, 0, "0.0%")]
    public void Percent_shows_one_decimal(decimal part, decimal whole, string expected)
    {
        Assert.Equal(expected, UsageNumberFormat.Percent(part, whole));
    }

    [Fact]
    public void Percent_of_a_fraction_survives_nonsense_input()
    {
        Assert.Equal("0.0%", UsageNumberFormat.Percent(double.NaN));
        Assert.Equal("0.0%", UsageNumberFormat.Percent(double.PositiveInfinity));
        Assert.Equal("99.2%", UsageNumberFormat.Percent(0.992d));
    }

    [Fact]
    public void Multiplier_compares_savings_against_cost()
    {
        Assert.Equal("6.7x", UsageNumberFormat.Multiplier(69_668.14m, 10_400.92m));
        Assert.Equal("0.0x", UsageNumberFormat.Multiplier(10m, 0m));
    }

    [Fact]
    public void Day_labels_match_the_dashboard_captions()
    {
        var from = new DateOnly(2026, 7, 25);
        var to = new DateOnly(2026, 8, 23);

        Assert.Equal("Jul 25 to Aug 23", UsageNumberFormat.RangeLabel(from, to));
        Assert.Equal("JUL 25", UsageNumberFormat.AxisDayLabel(from));
        Assert.Equal("Sun, Aug 23", UsageNumberFormat.LongDayLabel(to));
    }
}
