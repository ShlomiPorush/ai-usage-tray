using costats.Core.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class ModelPricingTests
{
    private static UsageTokens Tokens(
        long uncached = 0,
        long cacheRead = 0,
        long write5m = 0,
        long write1h = 0,
        long output = 0,
        long reasoning = 0) => new()
        {
            UncachedInputTokens = uncached,
            CacheReadInputTokens = cacheRead,
            CacheWrite5mInputTokens = write5m,
            CacheWrite1hInputTokens = write1h,
            OutputTokens = output,
            ReasoningOutputTokens = reasoning
        };

    [Fact]
    public void Anthropic_card_derives_cache_rates_from_the_input_rate()
    {
        var price = ModelPrice.Anthropic(10m, 50m);

        Assert.Equal(10m, price.InputPerMTok);
        Assert.Equal(1m, price.CachedInputPerMTok);      // 0.1x
        Assert.Equal(12.5m, price.CacheWrite5mPerMTok);  // 1.25x
        Assert.Equal(20m, price.CacheWrite1hPerMTok);    // 2x
        Assert.Equal(50m, price.OutputPerMTok);
        Assert.True(price.IsPriced);
    }

    [Fact]
    public void OpenAi_card_charges_nothing_for_cache_writes()
    {
        var price = ModelPrice.OpenAi(1.25m, 0.125m, 10m);

        Assert.Equal(0m, price.CacheWrite5mPerMTok);
        Assert.Equal(0m, price.CacheWrite1hPerMTok);
        Assert.True(price.IsPriced);
    }

    [Fact]
    public void OpenAi_cache_write_card_charges_one_and_a_quarter_input_on_both_ttl_buckets()
    {
        var price = ModelPrice.OpenAiWithCacheWrites(4m, 0.4m, 20m);

        Assert.Equal(4m, price.InputPerMTok);
        Assert.Equal(0.4m, price.CachedInputPerMTok);
        Assert.Equal(5m, price.CacheWrite5mPerMTok);
        Assert.Equal(5m, price.CacheWrite1hPerMTok);
        Assert.Equal(20m, price.OutputPerMTok);
        Assert.True(price.IsPriced);
    }

    [Fact]
    public void Cost_charges_every_bucket_at_its_own_rate()
    {
        // 1 MTok in each bucket at $10 input: 10 + 1 + 12.50 + 20 + 50 = $93.50
        var cost = UsageCostCalculator.Compute(
            Tokens(uncached: 1_000_000, cacheRead: 1_000_000, write5m: 1_000_000, write1h: 1_000_000, output: 1_000_000),
            ModelPrice.Anthropic(10m, 50m));

        Assert.True(cost.IsPriced);
        Assert.Equal(93.5m, cost.CostUsd);
    }

    [Fact]
    public void Cache_savings_is_what_full_rate_input_would_have_cost_minus_the_cache_read_rate()
    {
        // 2 MTok of cache reads at $5 input: full rate $10.00, cache rate $1.00, saved $9.00.
        var cost = UsageCostCalculator.Compute(Tokens(cacheRead: 2_000_000), ModelPrice.Anthropic(5m, 25m));

        Assert.Equal(1m, cost.CostUsd);
        Assert.Equal(9m, cost.CacheSavingsUsd);
    }

    [Fact]
    public void Cache_savings_is_zero_without_cache_reads()
    {
        var cost = UsageCostCalculator.Compute(Tokens(uncached: 1_000_000, output: 500_000), ModelPrice.Anthropic(5m, 25m));

        Assert.Equal(0m, cost.CacheSavingsUsd);
        Assert.Equal(17.5m, cost.CostUsd);
    }

    [Fact]
    public void Reasoning_tokens_are_not_charged_twice()
    {
        var withReasoning = UsageCostCalculator.Compute(
            Tokens(output: 1_000_000, reasoning: 900_000),
            ModelPrice.Anthropic(10m, 50m));
        var withoutReasoning = UsageCostCalculator.Compute(
            Tokens(output: 1_000_000),
            ModelPrice.Anthropic(10m, 50m));

        Assert.Equal(withoutReasoning.CostUsd, withReasoning.CostUsd);
    }

    [Fact]
    public void An_unknown_model_costs_nothing_and_is_flagged_unpriced()
    {
        var cost = UsageCostCalculator.Compute(Tokens(uncached: 5_000_000, output: 1_000_000), ModelPrice.Unpriced);

        Assert.False(cost.IsPriced);
        Assert.Equal(0m, cost.CostUsd);
        Assert.Equal(0m, cost.CacheSavingsUsd);
    }

    [Fact]
    public void A_null_price_is_treated_as_unpriced_rather_than_free()
    {
        var cost = UsageCostCalculator.Compute(Tokens(output: 1_000_000), null);

        Assert.False(cost.IsPriced);
        Assert.Equal(0m, cost.CostUsd);
    }

    [Fact]
    public void Unpriced_tokens_are_still_counted_and_the_model_is_listed()
    {
        var report = UsageAggregator.Aggregate(
            [
                new UsageSample(
                    DateTimeOffset.Parse("2026-08-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                    UsageProviderKind.Codex,
                    "codex",
                    "codex-auto-review",
                    Tokens(uncached: 1_000, output: 500))
            ],
            new UsageAggregationOptions { TimeZone = TimeZoneInfo.Utc });

        Assert.Equal(1_500, report.Totals.Tokens.ProcessedTokens);
        Assert.Equal(0m, report.Totals.CostUsd);
        Assert.Equal(1_500, report.Totals.UnpricedTokens);
        Assert.Equal(["codex-auto-review"], report.UnpricedModels);
        Assert.False(Assert.Single(report.ByModel).IsPriced);
    }

    [Fact]
    public void Default_table_prices_the_claude_models_the_logs_contain()
    {
        var table = ModelPricingTable.Default;

        Assert.Equal(10m, table.Find("claude-fable-5").InputPerMTok);
        Assert.Equal(50m, table.Find("claude-fable-5").OutputPerMTok);
        Assert.Equal(5m, table.Find("claude-opus-5").InputPerMTok);
        Assert.Equal(5m, table.Find("claude-opus-4-8").InputPerMTok);
        Assert.Equal(1m, table.Find("claude-haiku-4-5").InputPerMTok);
    }

    [Fact]
    public void Default_table_prices_the_openai_models_the_codex_logs_contain()
    {
        var table = ModelPricingTable.Default;

        // developers.openai.com per-model pricing tables, cross-checked against
        // the LiteLLM model-prices database (both retrieved 2026-08-23).
        var sol = table.Find("gpt-5.6-sol");
        Assert.Equal(4m, sol.InputPerMTok);
        Assert.Equal(0.4m, sol.CachedInputPerMTok);
        Assert.Equal(20m, sol.OutputPerMTok);
        Assert.Equal(5m, sol.CacheWrite5mPerMTok);

        var luna = table.Find("gpt-5.6-luna");
        Assert.Equal(0.2m, luna.InputPerMTok);
        Assert.Equal(0.02m, luna.CachedInputPerMTok);
        Assert.Equal(1.2m, luna.OutputPerMTok);
        Assert.Equal(0.25m, luna.CacheWrite5mPerMTok);

        Assert.Equal(2m, table.Find("gpt-5.6-terra").InputPerMTok);
        Assert.Equal(12.5m, table.Find("gpt-5.6-cyber").InputPerMTok);
        Assert.Equal(sol, table.Find("gpt-5.6"));

        // GPT-5.4 publishes no cache-write fee, so writes stay free.
        var v54 = table.Find("gpt-5.4");
        Assert.Equal(2.5m, v54.InputPerMTok);
        Assert.Equal(0.25m, v54.CachedInputPerMTok);
        Assert.Equal(15m, v54.OutputPerMTok);
        Assert.Equal(0m, v54.CacheWrite5mPerMTok);
    }

    [Fact]
    public void Codex_auto_review_is_known_but_stays_unpriced()
    {
        // Not an OpenAI API model: it is the Codex CLI's preferred-model hint
        // for approval reviews and the backend resolves it server-side, so no
        // published rate exists. Counted, never costed.
        var table = ModelPricingTable.Default;

        Assert.True(table.Entries.ContainsKey("codex-auto-review"));
        Assert.False(table.Find("codex-auto-review").IsPriced);
    }

    [Fact]
    public void A_sol_turn_is_costed_at_the_published_rates()
    {
        // 1 MTok uncached input, 10 MTok cache reads, 0.2 MTok output:
        // 4.00 + 4.00 + 4.00 = $12.00, and the cache saved 10 * (4 - 0.4) = $36.
        var cost = UsageCostCalculator.Compute(
            Tokens(uncached: 1_000_000, cacheRead: 10_000_000, output: 200_000),
            ModelPricingTable.Default.Find("gpt-5.6-sol"));

        Assert.True(cost.IsPriced);
        Assert.Equal(12m, cost.CostUsd);
        Assert.Equal(36m, cost.CacheSavingsUsd);
    }

    [Theory]
    [InlineData("claude-haiku-4-5-20251001", "claude-haiku-4-5")]
    [InlineData("claude-opus-4-5@20251101", "claude-opus-4-5")]
    [InlineData("anthropic.claude-opus-5", "claude-opus-5")]
    [InlineData("CLAUDE-OPUS-5", "claude-opus-5")]
    [InlineData("openai/gpt-5", "gpt-5")]
    public void Lookup_survives_vendor_prefixes_and_date_suffixes(string logged, string canonical)
    {
        var table = ModelPricingTable.Default;

        Assert.Equal(table.Find(canonical), table.Find(logged));
        Assert.True(table.Find(logged).IsPriced);
    }

    [Fact]
    public void An_id_that_matches_nothing_is_unpriced()
    {
        Assert.False(ModelPricingTable.Default.Find("not-a-model").IsPriced);
        Assert.False(ModelPricingTable.Default.Find(null).IsPriced);
        Assert.False(ModelPricingTable.Default.Find("   ").IsPriced);
    }

    [Fact]
    public void Overrides_replace_matching_models_and_add_new_ones()
    {
        var overrides = new ModelPricingTable(
        [
            new KeyValuePair<string, ModelPrice>("claude-opus-5", ModelPrice.Anthropic(99m, 999m)),
            new KeyValuePair<string, ModelPrice>("gpt-5.6-sol", ModelPrice.OpenAi(2m, 0.2m, 16m))
        ]);

        var merged = ModelPricingTable.Default.MergedWith(overrides);

        Assert.Equal(99m, merged.Find("claude-opus-5").InputPerMTok);
        Assert.Equal(2m, merged.Find("gpt-5.6-sol").InputPerMTok);
        Assert.True(merged.Find("gpt-5.6-sol").IsPriced);
        // Untouched models keep the built-in rates, and the source table is unchanged.
        Assert.Equal(10m, merged.Find("claude-fable-5").InputPerMTok);
        Assert.Equal(5m, ModelPricingTable.Default.Find("claude-opus-5").InputPerMTok);
    }

    [Fact]
    public void Merging_nothing_returns_the_same_table()
    {
        Assert.Same(ModelPricingTable.Default, ModelPricingTable.Default.MergedWith(null));
        Assert.Same(ModelPricingTable.Default, ModelPricingTable.Default.MergedWith(new ModelPricingTable([])));
    }
}
