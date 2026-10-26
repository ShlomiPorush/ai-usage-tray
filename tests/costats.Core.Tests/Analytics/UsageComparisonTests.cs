using costats.Core.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class UsageComparisonTests
{
    private static UsageTotals Totals(decimal cost, long tokens, long unpriced = 0) => new()
    {
        CostUsd = cost,
        Tokens = new UsageTokens { UncachedInputTokens = tokens },
        UnpricedTokens = unpriced,
        RequestCount = tokens > 0 ? 1 : 0
    };

    private static ModelUsage Model(string model, UsageProviderKind provider, decimal cost, long tokens, bool priced = true) =>
        new(model, provider, priced, Totals(cost, tokens, priced ? 0 : tokens));

    private static UsageReport Report(params ModelUsage[] models) => new()
    {
        ByModel = models,
        Totals = models.Aggregate(UsageTotals.Empty, (sum, model) => sum.Add(model.Totals))
    };

    [Fact]
    public void Fully_priced_totals_compare_by_cost()
    {
        var delta = UsageDelta.Between(Totals(15m, 100), Totals(10m, 400));

        Assert.Equal(UsageDeltaKind.Change, delta.Kind);
        Assert.Equal(UsageDeltaBasis.Cost, delta.Basis);
        Assert.Equal(0.5d, delta.Fraction, 6);
    }

    [Fact]
    public void Unpriced_usage_on_either_side_compares_tokens_instead()
    {
        // $10 + lots unpriced against $20 + a little: cost would claim +100%.
        var delta = UsageDelta.Between(Totals(20m, 200, unpriced: 10), Totals(10m, 800, unpriced: 700));

        Assert.Equal(UsageDeltaBasis.Tokens, delta.Basis);
        Assert.Equal(-0.75d, delta.Fraction, 6);
    }

    [Fact]
    public void Periods_of_different_lengths_compare_the_average_per_day()
    {
        // $70 over 7 days against $140 over 14 days: the same $10 a day.
        var same = UsageDelta.Between(Totals(70m, 700), Totals(140m, 1_400), previousScale: 7d / 14d);
        Assert.Equal(0d, same.Fraction, 6);

        // $70 over 7 days against $70 over 14 days: twice as much per day.
        var doubled = UsageDelta.Between(Totals(70m, 700), Totals(70m, 700), previousScale: 7d / 14d);
        Assert.Equal(1d, doubled.Fraction, 6);
    }

    [Fact]
    public void An_empty_baseline_is_no_prior_data_not_new()
    {
        Assert.Equal(UsageDeltaKind.NoPriorData, UsageDelta.Between(Totals(5m, 10), UsageTotals.Empty).Kind);
        Assert.Equal(UsageDeltaKind.None, UsageDelta.Between(UsageTotals.Empty, UsageTotals.Empty).Kind);

        var gone = UsageDelta.Between(UsageTotals.Empty, Totals(5m, 10));
        Assert.Equal(-1d, gone.Fraction, 6);
    }

    [Theory]
    [InlineData(0.124, UsageDeltaBasis.Cost, "▲ 12.4%")]
    [InlineData(-0.03, UsageDeltaBasis.Cost, "▼ 3.0%")]
    [InlineData(2.5, UsageDeltaBasis.Cost, "▲ 250%")]
    [InlineData(12.0, UsageDeltaBasis.Tokens, "▲ 1,200% tokens")]
    [InlineData(0.0001, UsageDeltaBasis.Cost, "0.0%")]
    [InlineData(-1.0, UsageDeltaBasis.Cost, "▼ 100%")]
    public void Deltas_format_with_a_neutral_arrow(double fraction, UsageDeltaBasis basis, string expected)
    {
        Assert.Equal(expected, UsageNumberFormat.Delta(new UsageDelta(UsageDeltaKind.Change, fraction, basis)));
    }

    [Fact]
    public void Token_figures_can_drop_the_redundant_basis_suffix()
    {
        var delta = new UsageDelta(UsageDeltaKind.Change, 0.372, UsageDeltaBasis.Tokens);

        Assert.Equal("\u25B2 37.2% tokens", UsageNumberFormat.Delta(delta));
        Assert.Equal("\u25B2 37.2%", UsageNumberFormat.Delta(delta, showBasis: false));
    }

    [Fact]
    public void Deltas_without_a_baseline_say_so()
    {
        Assert.Equal("no prior data", UsageNumberFormat.Delta(new UsageDelta(UsageDeltaKind.NoPriorData, 0d, UsageDeltaBasis.Cost)));
        Assert.Equal(string.Empty, UsageNumberFormat.Delta(UsageDelta.None));
    }

    [Fact]
    public void Models_only_the_comparison_used_stay_as_a_drop_to_zero()
    {
        var current = Report(Model("opus", UsageProviderKind.Claude, 5m, 50));
        var previous = Report(
            Model("opus", UsageProviderKind.Claude, 10m, 100),
            Model("sonnet", UsageProviderKind.Claude, 4m, 40));

        var rows = UsageBreakdown.Models(current, previous);
        var sonnet = Assert.Single(rows, row => row.Label == "sonnet");

        Assert.Equal(2, rows.Count);
        Assert.Equal(0m, sonnet.Totals.CostUsd);
        Assert.Equal(4m, sonnet.PreviousTotals!.CostUsd);
        Assert.Equal(10m, rows.Single(row => row.Label == "opus").PreviousTotals!.CostUsd);
        Assert.Equal(-1d, sonnet.Delta.Fraction, 6);
        Assert.Equal(-0.5d, rows.Single(row => row.Label == "opus").Delta.Fraction, 6);
    }

    [Fact]
    public void The_same_model_id_under_two_providers_stays_two_rows()
    {
        var current = Report(
            Model("shared", UsageProviderKind.Claude, 1m, 10),
            Model("shared", UsageProviderKind.Codex, 2m, 20));
        var previous = Report(Model("shared", UsageProviderKind.Codex, 1m, 10));

        var rows = UsageBreakdown.Models(current, previous);

        Assert.Equal(2, rows.Count);
        Assert.Equal(UsageDeltaKind.NoPriorData, rows.Single(row => row.Provider == UsageProviderKind.Claude).Delta.Kind);
        Assert.Equal(1d, rows.Single(row => row.Provider == UsageProviderKind.Codex).Delta.Fraction, 6);
    }

    [Fact]
    public void Model_rows_compare_the_average_per_day_across_lengths()
    {
        // 7 days at $1 a day against 14 days at $1 a day: no change.
        var rows = UsageBreakdown.Models(
            Report(Model("opus", UsageProviderKind.Claude, 7m, 70)),
            Report(Model("opus", UsageProviderKind.Claude, 14m, 140)),
            new UsageChangeDirection(false, 7d / 14d));

        Assert.Equal(0d, Assert.Single(rows).Delta.Fraction, 6);
    }

    [Fact]
    public void Changes_run_from_the_earlier_period_to_the_later_one()
    {
        // A is September, B is October: $413 then $870 is a rise, not a drop.
        var september = new UsageResolvedPeriod(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), null, 30);
        var october = new UsageResolvedPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 30), null, 30);

        var forward = UsageChangeDirection.For(september, october, TimeZoneInfo.Utc);
        var rise = forward.Between(Totals(413m, 1), Totals(870m, 1));

        Assert.True(forward.CurrentIsEarlier);
        Assert.Equal("A \u2192 B", forward.Label);
        Assert.True(rise.Fraction > 1.1 && rise.Fraction < 1.11);

        // The default view, A on screen and B before it, measures B to A.
        var backward = UsageChangeDirection.For(october, september, TimeZoneInfo.Utc);
        Assert.False(backward.CurrentIsEarlier);
        Assert.Equal("B \u2192 A", backward.Label);
        Assert.True(backward.Between(Totals(870m, 1), Totals(413m, 1)).Fraction > 1.1);
    }

    [Fact]
    public void A_later_period_with_no_usage_before_it_says_no_prior_data()
    {
        var early = new UsageResolvedPeriod(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 7), null, 7);
        var late = new UsageResolvedPeriod(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 7), null, 7);

        // A (early) had nothing, B (late) had usage: nothing before to compare with.
        var delta = UsageChangeDirection.For(early, late, TimeZoneInfo.Utc).Between(UsageTotals.Empty, Totals(5m, 5));

        Assert.Equal(UsageDeltaKind.NoPriorData, delta.Kind);
    }

    [Fact]
    public void Lengths_scale_the_same_way_in_either_direction()
    {
        // A: 7 days at $10 a day, B: 14 days later at $10 a day. No change.
        var a = new UsageResolvedPeriod(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 7), null, 7);
        var b = new UsageResolvedPeriod(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 14), null, 14);

        Assert.Equal(0d, UsageChangeDirection.For(a, b, TimeZoneInfo.Utc).Between(Totals(70m, 1), Totals(140m, 1)).Fraction, 6);
        Assert.Equal(0d, UsageChangeDirection.For(b, a, TimeZoneInfo.Utc).Between(Totals(140m, 1), Totals(70m, 1)).Fraction, 6);
    }

    [Fact]
    public void Periods_starting_together_are_ordered_by_their_exact_end()
    {
        // A: the 24 hours ending at local midnight Oct 26. B: Oct 25 and 26.
        // Both start at midnight Oct 25; A ends first, so the change runs A to B.
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");
        var since = UsagePeriods.StartOfDay(new DateOnly(2026, 10, 25), zone);
        var a = new UsageResolvedPeriod(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26), new UsageTimeWindow(since, since.AddHours(24)), 1);
        var b = new UsageResolvedPeriod(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26), null, 2);

        var direction = UsageChangeDirection.For(a, b, zone);

        // $10 in one day against $40 over two: twice as much per day, a rise.
        Assert.True(direction.CurrentIsEarlier);
        Assert.Equal(1d, direction.Between(Totals(10m, 1), Totals(40m, 1)).Fraction, 6);
    }

    [Fact]
    public void Day_ranges_are_ordered_by_real_local_midnights()
    {
        // In UTC+14 local midnight Oct 25 is Oct 24 10:00 UTC, before a window
        // starting Oct 24 12:00 UTC, so the day range is the earlier period.
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+14", TimeSpan.FromHours(14), "Test+14", "Test+14");
        var days = new UsageResolvedPeriod(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 25), null, 1);
        var windowStart = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var window = new UsageResolvedPeriod(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26), new UsageTimeWindow(windowStart, windowStart.AddHours(24)), 1);

        Assert.True(UsageChangeDirection.For(days, window, zone).CurrentIsEarlier);
    }

    [Fact]
    public void Token_and_cost_deltas_scale_the_same_way()
    {
        Assert.Equal(0d, UsageDelta.OfTokens(100, 200, 0.5d).Fraction, 6);
        Assert.Equal(1d, UsageDelta.OfCost(10m, 10m, 0.5d).Fraction, 6);
    }

    [Fact]
    public void An_unpriced_model_only_b_used_is_unpriced_in_b_but_not_in_a()
    {
        var rows = UsageBreakdown.Models(
            Report(Model("opus", UsageProviderKind.Claude, 5m, 50)),
            Report(Model("mystery", UsageProviderKind.Codex, 0m, 30, priced: false)));
        var mystery = rows.Single(row => row.Label == "mystery");

        Assert.False(mystery.IsUnpriced);
        Assert.True(mystery.IsPreviousUnpriced);
        Assert.Equal(0m, mystery.Totals.CostUsd);
    }

    [Fact]
    public void Without_a_comparison_rows_carry_no_delta()
    {
        var rows = UsageBreakdown.Models(Report(Model("opus", UsageProviderKind.Claude, 5m, 50)), null);

        Assert.Equal(UsageDeltaKind.None, Assert.Single(rows).Delta.Kind);
        Assert.Null(Assert.Single(rows).PreviousTotals);
    }
}
