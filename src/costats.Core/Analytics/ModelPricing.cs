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
/// Model id to <see cref="ModelPrice"/>. Ships a default table built from the
/// bundled price snapshot and can be merged with newer catalogs and a user
/// override, so a new model can be priced without a release.
/// </summary>
public sealed class ModelPricingTable
{
    private static readonly Regex DateSuffix = new(@"[-@]\d{8}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string SnapshotResource = "costats.Core.Analytics.pricing-snapshot.json";

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
    /// Models that stay unpriced whatever a catalog says. Only the user's
    /// override file can price them.
    /// <para>
    /// <c>codex-auto-review</c> is not an OpenAI API model: it is the
    /// preferred-model hint the Codex CLI sends on the subscription path
    /// (<c>DEFAULT_APPROVAL_REVIEW_PREFERRED_MODEL</c> in
    /// <c>codex-rs/model-provider/src/provider.rs</c>), the API rejects it as an
    /// unsupported model name (openai/codex issue 31255), and the backend
    /// resolves it to a real model server-side (openai/codex PR 23767). It has
    /// no published rate, and the candidate mappings differ by more than 10x,
    /// so its tokens are counted and reported as unpriced rather than guessed.
    /// A wrong price would be worse than none.
    /// </para>
    /// </summary>
    public static ModelPricingTable PinnedUnpriced { get; } = new(
    [
        Entry("codex-auto-review", ModelPrice.Unpriced)
    ]);

    /// <summary>
    /// The table shipped with the app: the bundled LiteLLM snapshot
    /// (<c>Analytics/pricing-snapshot.json</c>, generated by
    /// <c>scripts/update-pricing-snapshot.mjs</c>) with
    /// <see cref="PinnedUnpriced"/> on top. At runtime the app layers the live
    /// catalog and the user's <c>pricing.json</c> over it; this is what prices
    /// the report when neither is available.
    /// </summary>
    /// <remarks>Declared after <see cref="PinnedUnpriced"/>: static initializers run in order.</remarks>
    public static ModelPricingTable Default { get; } = LoadSnapshot().MergedWith(PinnedUnpriced);

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

    private static ModelPricingTable LoadSnapshot()
    {
        using var stream = typeof(ModelPricingTable).Assembly.GetManifestResourceStream(SnapshotResource)
            ?? throw new InvalidOperationException("The bundled price snapshot is missing from the build.");
        using var reader = new StreamReader(stream);
        return LiteLlmPricingCatalog.TryParse(reader.ReadToEnd())
            ?? throw new InvalidOperationException("The bundled price snapshot is not valid JSON.");
    }

    private static KeyValuePair<string, ModelPrice> Entry(string model, ModelPrice price) => new(model, price);
}
