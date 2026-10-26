using costats.Core.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class UsageBreakdownSortTests
{
    private static UsageBreakdownItem Item(
        string label,
        decimal cost = 0m,
        long tokens = 0,
        bool unpriced = false,
        UsageProviderKind? provider = UsageProviderKind.Claude,
        UsageDelta delta = default,
        DateTimeOffset? time = null) => new()
    {
        Label = label,
        Provider = provider,
        Time = time,
        Totals = new UsageTotals { CostUsd = cost, Tokens = new UsageTokens { OutputTokens = tokens } },
        IsUnpriced = unpriced,
        Delta = delta
    };

    private static string[] Labels(IEnumerable<UsageBreakdownItem> items) => items.Select(item => item.Label).ToArray();

    [Fact]
    public void Tokens_sort_by_raw_counts_not_by_their_text()
    {
        // As text, "900M" would sort above "1.20B".
        var items = new[] { Item("small", tokens: 900_000_000), Item("big", tokens: 1_200_000_000) };

        Assert.Equal(["big", "small"], Labels(UsageBreakdown.Sort(items, new(UsageSortColumn.Tokens, true))));
        Assert.Equal(["small", "big"], Labels(UsageBreakdown.Sort(items, new(UsageSortColumn.Tokens, false))));
    }

    [Theory]
    [InlineData(UsageSortColumn.Value)]
    [InlineData(UsageSortColumn.Share)]
    public void Unpriced_rows_go_last_in_both_directions(UsageSortColumn column)
    {
        var items = new[] { Item("free", unpriced: true), Item("cheap", 1m), Item("dear", 9m) };

        Assert.Equal(["dear", "cheap", "free"], Labels(UsageBreakdown.Sort(items, new(column, true))));
        Assert.Equal(["cheap", "dear", "free"], Labels(UsageBreakdown.Sort(items, new(column, false))));
    }

    [Fact]
    public void No_prior_data_ranks_above_any_rise_and_no_delta_goes_last()
    {
        var items = new[]
        {
            Item("flat"),
            Item("up", delta: new UsageDelta(UsageDeltaKind.Change, 5d, UsageDeltaBasis.Cost)),
            Item("fresh", delta: new UsageDelta(UsageDeltaKind.NoPriorData, 0d, UsageDeltaBasis.Cost)),
            Item("down", delta: new UsageDelta(UsageDeltaKind.Change, -0.5d, UsageDeltaBasis.Cost))
        };

        Assert.Equal(["fresh", "up", "down", "flat"], Labels(UsageBreakdown.Sort(items, new(UsageSortColumn.Change, true))));
        Assert.Equal(["down", "up", "fresh", "flat"], Labels(UsageBreakdown.Sort(items, new(UsageSortColumn.Change, false))));
    }

    [Fact]
    public void Period_b_columns_sort_by_their_own_values_with_unpriced_last()
    {
        var items = new[]
        {
            Item("a") with { PreviousTotals = new UsageTotals { CostUsd = 5m, Tokens = new UsageTokens { OutputTokens = 10 } } },
            Item("b") with { PreviousTotals = new UsageTotals { CostUsd = 9m, Tokens = new UsageTokens { OutputTokens = 1 } } },
            Item("c") with { PreviousTotals = new UsageTotals { Tokens = new UsageTokens { OutputTokens = 99 } }, IsPreviousUnpriced = true }
        };

        Assert.Equal(["b", "a", "c"], Labels(UsageBreakdown.Sort(items, new(UsageSortColumn.PreviousValue, true))));
        Assert.Equal(["a", "b", "c"], Labels(UsageBreakdown.Sort(items, new(UsageSortColumn.PreviousValue, false))));
        Assert.Equal(["c", "a", "b"], Labels(UsageBreakdown.Sort(items, new(UsageSortColumn.PreviousTokens, true))));
    }

    [Fact]
    public void Time_rows_sort_by_time_not_by_label_text()
    {
        var items = new[]
        {
            Item("Fri, Oct 9", provider: null, time: new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero)),
            Item("Sat, Oct 10", provider: null, time: new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero))
        };

        Assert.Equal(["Sat, Oct 10", "Fri, Oct 9"], Labels(UsageBreakdown.Sort(items, UsageSortState.TimeDefault)));
    }

    [Fact]
    public void Ties_fall_back_to_label_then_provider()
    {
        var items = new[]
        {
            Item("b", 1m, provider: UsageProviderKind.Codex),
            Item("a", 1m, provider: UsageProviderKind.Codex),
            Item("a", 1m, provider: UsageProviderKind.Claude)
        };

        var sorted = UsageBreakdown.Sort(items, new(UsageSortColumn.Value, true));

        Assert.Equal(["a", "a", "b"], Labels(sorted));
        Assert.Equal(UsageProviderKind.Claude, sorted[0].Provider);
    }

    [Fact]
    public void Toggle_flips_the_same_column_and_starts_new_ones_descending()
    {
        var sort = UsageSortState.ModelDefault;

        Assert.Equal(new UsageSortState(UsageSortColumn.Value, false), sort.Toggle(UsageSortColumn.Value, false));
        Assert.Equal(new UsageSortState(UsageSortColumn.Tokens, true), sort.Toggle(UsageSortColumn.Tokens, false));
        Assert.Equal(new UsageSortState(UsageSortColumn.Label, false), sort.Toggle(UsageSortColumn.Label, false));
        Assert.Equal(new UsageSortState(UsageSortColumn.Label, true), sort.Toggle(UsageSortColumn.Label, true));
    }
}
