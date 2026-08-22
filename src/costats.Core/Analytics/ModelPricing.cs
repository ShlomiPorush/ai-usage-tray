using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace costats.Core.Analytics;

/// <summary>
/// Raw API list prices for one model, in US dollars per million tokens
/// (per-MTok). A <see langword="null"/> rate means "we do not know this price",
/// never "free": see <see cref="IsPriced"/>.
/// </summary>
public sealed record ModelPrice
{
    /// <summary>A model we count but cannot price. All rates unknown.</summary>
    public static readonly ModelPrice Unpriced = new();

    /// <summary>Full-rate input, USD per million tokens.</summary>
    public decimal? InputPerMTok { get; init; }

    /// <summary>Cache-hit input (cache read), USD per million tokens.</summary>
    public decimal? CachedInputPerMTok { get; init; }

    /// <summary>Writing into the 5-minute cache, USD per million tokens.</summary>
    public decimal? CacheWrite5mPerMTok { get; init; }

    /// <summary>Writing into the 1-hour cache, USD per million tokens.</summary>
    public decimal? CacheWrite1hPerMTok { get; init; }

    /// <summary>Generated output, USD per million tokens.</summary>
    public decimal? OutputPerMTok { get; init; }

    /// <summary>
    /// True when the two rates every request needs are known. A missing cache
    /// component rate is then treated as zero, which is correct for providers
    /// that do not bill cache writes at all.
    /// </summary>
    public bool IsPriced => InputPerMTok.HasValue && OutputPerMTok.HasValue;

    /// <summary>
    /// Builds an Anthropic-shaped card from the two headline rates. Anthropic
    /// derives the rest from the input rate: cache read is 0.1x, a 5-minute
    /// cache write is 1.25x and a 1-hour cache write is 2x.
    /// </summary>
    public static ModelPrice Anthropic(decimal inputPerMTok, decimal outputPerMTok) => new()
    {
        InputPerMTok = inputPerMTok,
        CachedInputPerMTok = inputPerMTok * 0.1m,
        CacheWrite5mPerMTok = inputPerMTok * 1.25m,
        CacheWrite1hPerMTok = inputPerMTok * 2m,
        OutputPerMTok = outputPerMTok
    };

    /// <summary>
    /// Builds an OpenAI-shaped card. OpenAI publishes the cached-input rate
    /// directly and does not bill for cache writes, so both write rates stay
    /// zero rather than unknown.
    /// </summary>
    public static ModelPrice OpenAi(decimal inputPerMTok, decimal cachedInputPerMTok, decimal outputPerMTok) => new()
    {
        InputPerMTok = inputPerMTok,
        CachedInputPerMTok = cachedInputPerMTok,
        CacheWrite5mPerMTok = 0m,
        CacheWrite1hPerMTok = 0m,
        OutputPerMTok = outputPerMTok
    };

    /// <summary>
    /// Builds an OpenAI-shaped card for a model that does bill cache writes.
    /// The GPT-5.6 family charges a write at 1.25x the uncached input rate
    /// ("Cache writes are billed at 1.25x the uncached input token rate",
    /// https://developers.openai.com/api/docs/models/gpt-5.6-sol, retrieved
    /// 2026-08-23) and publishes no TTL tiers, so the single write rate is put
    /// on both buckets: whichever one a parser fills, the charge is the same.
    /// </summary>
    public static ModelPrice OpenAiWithCacheWrites(decimal inputPerMTok, decimal cachedInputPerMTok, decimal outputPerMTok) => new()
    {
        InputPerMTok = inputPerMTok,
        CachedInputPerMTok = cachedInputPerMTok,
        CacheWrite5mPerMTok = inputPerMTok * 1.25m,
        CacheWrite1hPerMTok = inputPerMTok * 1.25m,
        OutputPerMTok = outputPerMTok
    };
}

/// <summary>
/// Cost and cache savings for one bucket of tokens.
/// </summary>
/// <param name="CostUsd">
/// What the same traffic would have cost at raw API list prices. Zero for an
/// unpriced model.
/// </param>
/// <param name="CacheSavingsUsd">
/// What prompt caching saved: the difference between paying full input rate for
/// every cache-read token and the cache-read rate actually charged.
/// </param>
/// <param name="IsPriced">False when the model has no known rates.</param>
public readonly record struct UsageCost(decimal CostUsd, decimal CacheSavingsUsd, bool IsPriced);

/// <summary>
/// Turns token counts into dollars. Every formula is list-price arithmetic; no
/// subscription, discount or tier is modelled.
/// </summary>
public static class UsageCostCalculator
{
    /// <summary>Rates are quoted per this many tokens.</summary>
    public const decimal TokensPerMillion = 1_000_000m;

    /// <summary>
    /// Costs a bucket of tokens.
    /// <para>
    /// <c>cost = (uncachedInput * input + cacheRead * cachedInput
    /// + cacheWrite5m * write5m + cacheWrite1h * write1h + output * output) / 1e6</c>
    /// </para>
    /// <para>
    /// <c>cacheSavings = cacheRead * (input - cachedInput) / 1e6</c>, that is
    /// what those tokens would have cost at the full input rate minus what the
    /// cache-read rate actually charged. It is a saving, not part of the cost.
    /// </para>
    /// <para>
    /// Output already includes reasoning and thinking tokens, so
    /// <see cref="UsageTokens.ReasoningOutputTokens"/> is deliberately not
    /// charged again.
    /// </para>
    /// An unpriced model costs 0 and saves 0, and is reported separately so a
    /// zero is never mistaken for "free".
    /// </summary>
    public static UsageCost Compute(UsageTokens tokens, ModelPrice? price)
    {
        if (price is null || !price.IsPriced)
        {
            return new UsageCost(0m, 0m, false);
        }

        var input = price.InputPerMTok ?? 0m;
        var cachedInput = price.CachedInputPerMTok ?? 0m;
        var write5m = price.CacheWrite5mPerMTok ?? 0m;
        var write1h = price.CacheWrite1hPerMTok ?? 0m;
        var output = price.OutputPerMTok ?? 0m;

        var cost =
            (tokens.UncachedInputTokens * input) +
            (tokens.CacheReadInputTokens * cachedInput) +
            (tokens.CacheWrite5mInputTokens * write5m) +
            (tokens.CacheWrite1hInputTokens * write1h) +
            (tokens.OutputTokens * output);

        var savings = tokens.CacheReadInputTokens * (input - cachedInput);

        return new UsageCost(cost / TokensPerMillion, savings / TokensPerMillion, true);
    }
}

/// <summary>
/// Model id to <see cref="ModelPrice"/>. Ships a built-in default table and can
/// be merged with a user override so a new model can be priced without a
/// release.
/// </summary>
public sealed class ModelPricingTable
{
    private static readonly Regex DateSuffix = new(@"[-@]\d{8}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, ModelPrice> _entries;

    /// <summary>Builds a table from explicit entries. Later duplicates win.</summary>
    public ModelPricingTable(IEnumerable<KeyValuePair<string, ModelPrice>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null)
            {
                continue;
            }

            _entries[Normalize(entry.Key)] = entry.Value;
        }
    }

    /// <summary>Every known model id, normalised, with its rates.</summary>
    public IReadOnlyDictionary<string, ModelPrice> Entries => _entries;

    /// <summary>
    /// The table shipped with the app.
    /// <para>
    /// Anthropic rates come from the bundled <c>claude-api</c> skill reference:
    /// its model table gives input and output per MTok, and its prompt-caching
    /// note gives the derived multipliers (cache read 0.1x, 5-minute write
    /// 1.25x, 1-hour write 2x). Anthropic models older than that table are
    /// carried over from this repository's legacy
    /// <c>costats.Core.Pulse.TariffRegistry</c>.
    /// </para>
    /// <para>
    /// OpenAI GPT-5.4 and GPT-5.6 rates come from the per-model pricing tables
    /// on <c>developers.openai.com/api/docs/models/&lt;id&gt;</c> (retrieved
    /// 2026-08-23), cross-checked entry by entry against the LiteLLM
    /// model-prices database that most third-party cost dashboards consume
    /// (https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json,
    /// retrieved 2026-08-23); the two agree exactly. Older OpenAI rates are
    /// carried over from this repository's legacy
    /// <c>costats.Core.Pulse.TariffRegistry</c>.
    /// </para>
    /// <para>
    /// Only list prices are modelled. OpenAI also bills a long-context tier
    /// (prompts over 272K input tokens cost 2x input and 1.5x output for the
    /// whole request) plus batch, flex and priority tiers; none of them can be
    /// recovered from a per-turn token total, so costs here are a lower bound
    /// for sessions that run past 272K of context.
    /// </para>
    /// <para>
    /// <c>codex-auto-review</c> stays unpriced on purpose. It is not an OpenAI
    /// API model: it is the preferred-model hint the Codex CLI sends on the
    /// subscription path (<c>DEFAULT_APPROVAL_REVIEW_PREFERRED_MODEL</c> in
    /// <c>codex-rs/model-provider/src/provider.rs</c>), the API rejects it as an
    /// unsupported model name (openai/codex issue 31255), and the backend
    /// resolves it to a real model server-side (openai/codex PR 23767). It has
    /// no published rate anywhere, and the candidate mappings differ by more
    /// than 10x, so its tokens are counted and reported as unpriced rather than
    /// guessed. A wrong price would be worse than none; a user who knows what
    /// their reviews resolve to can price it in <c>pricing.json</c>.
    /// </para>
    /// </summary>
    public static ModelPricingTable Default { get; } = new(
    [
        // Anthropic, current generation (claude-api skill model table).
        Entry("claude-fable-5", ModelPrice.Anthropic(10m, 50m)),
        Entry("claude-mythos-5", ModelPrice.Anthropic(10m, 50m)),
        Entry("claude-opus-5", ModelPrice.Anthropic(5m, 25m)),
        Entry("claude-opus-4-8", ModelPrice.Anthropic(5m, 25m)),
        Entry("claude-opus-4-7", ModelPrice.Anthropic(5m, 25m)),
        Entry("claude-opus-4-6", ModelPrice.Anthropic(5m, 25m)),
        // Sonnet 5 also has a lower introductory rate; the standard rate is used
        // so the table does not silently expire.
        Entry("claude-sonnet-5", ModelPrice.Anthropic(3m, 15m)),
        Entry("claude-sonnet-4-6", ModelPrice.Anthropic(3m, 15m)),
        Entry("claude-haiku-4-5", ModelPrice.Anthropic(1m, 5m)),

        // Anthropic, older models carried over from TariffRegistry.
        Entry("claude-opus-4-5", ModelPrice.Anthropic(5m, 25m)),
        Entry("claude-sonnet-4-5", ModelPrice.Anthropic(3m, 15m)),
        Entry("claude-opus-4-1", ModelPrice.Anthropic(15m, 75m)),
        Entry("claude-opus-4-0", ModelPrice.Anthropic(15m, 75m)),
        Entry("claude-sonnet-4-0", ModelPrice.Anthropic(3m, 15m)),

        // OpenAI, rates carried over from TariffRegistry.
        Entry("gpt-5", ModelPrice.OpenAi(1.25m, 0.125m, 10m)),
        Entry("gpt-5.2", ModelPrice.OpenAi(1.75m, 0.175m, 14m)),
        Entry("o3", ModelPrice.OpenAi(10m, 2.5m, 40m)),
        Entry("o4-mini", ModelPrice.OpenAi(1.1m, 0.275m, 4.4m)),

        // OpenAI GPT-5.4. Published with a cached-input rate and no cache-write
        // fee: https://developers.openai.com/api/docs/models/gpt-5.4 (2026-08-23).
        Entry("gpt-5.4", ModelPrice.OpenAi(2.5m, 0.25m, 15m)),
        Entry("gpt-5.5", ModelPrice.OpenAi(5m, 0.5m, 30m)),

        // OpenAI GPT-5.6 family. Unlike earlier OpenAI models these do bill
        // cache writes, at 1.25x the uncached input rate.
        // https://developers.openai.com/api/docs/models/gpt-5.6-sol (2026-08-23)
        // and the sibling gpt-5.6-terra / gpt-5.6-luna / gpt-5.6-cyber pages.
        // "gpt-5.6" bare is the sol tier.
        Entry("gpt-5.6", ModelPrice.OpenAiWithCacheWrites(4m, 0.4m, 20m)),
        Entry("gpt-5.6-sol", ModelPrice.OpenAiWithCacheWrites(4m, 0.4m, 20m)),
        Entry("gpt-5.6-terra", ModelPrice.OpenAiWithCacheWrites(2m, 0.2m, 12m)),
        Entry("gpt-5.6-luna", ModelPrice.OpenAiWithCacheWrites(0.2m, 0.02m, 1.2m)),
        Entry("gpt-5.6-cyber", ModelPrice.OpenAiWithCacheWrites(12.5m, 1.25m, 75m)),

        // Not an API model and not published anywhere: see the remarks above.
        // Counted, never costed, always reported as unpriced.
        Entry("codex-auto-review", ModelPrice.Unpriced)
    ]);

    /// <summary>
    /// Looks a model up, falling back to its date-stripped id (so
    /// <c>claude-haiku-4-5-20251001</c> finds <c>claude-haiku-4-5</c>).
    /// Returns <see cref="ModelPrice.Unpriced"/> when nothing matches, so
    /// callers never have to null-check.
    /// </summary>
    public ModelPrice Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return ModelPrice.Unpriced;
        }

        var normalized = Normalize(model);
        if (_entries.TryGetValue(normalized, out var price))
        {
            return price;
        }

        var undated = DateSuffix.Replace(normalized, string.Empty);
        return !string.Equals(undated, normalized, StringComparison.Ordinal) &&
               _entries.TryGetValue(undated, out var datedPrice)
            ? datedPrice
            : ModelPrice.Unpriced;
    }

    /// <summary>True when the model has usable rates in this table.</summary>
    public bool IsPriced([NotNullWhen(true)] string? model) => Find(model).IsPriced;

    /// <summary>
    /// Returns a copy of this table with <paramref name="overrides"/> layered on
    /// top: an id present in both takes the override's rates, ids only in the
    /// override are added, and everything else is kept.
    /// </summary>
    public ModelPricingTable MergedWith(ModelPricingTable? overrides)
    {
        if (overrides is null || overrides._entries.Count == 0)
        {
            return this;
        }

        var merged = new Dictionary<string, ModelPrice>(_entries, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in overrides._entries)
        {
            merged[entry.Key] = entry.Value;
        }

        return new ModelPricingTable(merged);
    }

    /// <summary>
    /// Canonical form of a model id: trimmed, lowercased, and stripped of the
    /// <c>anthropic.</c> / <c>openai/</c> vendor prefixes that appear on
    /// partner-hosted ids.
    /// </summary>
    public static string Normalize(string? model)
    {
        var trimmed = model?.Trim() ?? string.Empty;
        if (trimmed.StartsWith("anthropic.", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[10..];
        }
        else if (trimmed.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..];
        }

        return trimmed.ToLowerInvariant();
    }

    private static KeyValuePair<string, ModelPrice> Entry(string model, ModelPrice price) => new(model, price);
}
