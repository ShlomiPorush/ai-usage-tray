using System.Text.Json;
using System.Text.Json.Nodes;

namespace costats.Core.Analytics;

/// <summary>
/// Reads the LiteLLM model-prices catalog
/// (<c>model_prices_and_context_window.json</c> in BerriAI/litellm), the
/// community-maintained price list most third-party cost dashboards consume.
/// </summary>
/// <remarks>
/// <para>
/// Only first-party Anthropic and OpenAI chat models are read: the logs this
/// app scans come from Claude Code and Codex, and partner-hosted entries
/// (Bedrock, Vertex, Azure) carry partner prices under ids that normalise onto
/// the first-party ones. An entry needs both an input and an output rate to be
/// used, and a rate outside 0 to <see cref="MaxRatePerMTok"/> is treated as
/// unknown rather than trusted.
/// </para>
/// <para>
/// The catalog quotes USD per token. Rates are converted to USD per million
/// tokens. A missing 1-hour cache-write rate falls back to the 5-minute one:
/// OpenAI publishes a single cache-write rate with no TTL tiers, so whichever
/// bucket a parser fills is charged the same.
/// </para>
/// <para>
/// Only list prices are read. Long-context, batch, flex and priority tiers are
/// ignored, so costs are a lower bound for sessions that cross a long-context
/// threshold.
/// </para>
/// </remarks>
public static class LiteLlmPricingCatalog
{
    /// <summary>
    /// Fewest usable models a downloaded catalog must hold to be accepted. A
    /// smaller result means the file changed shape, not that prices vanished.
    /// </summary>
    public const int MinimumModels = 20;

    /// <summary>Highest rate accepted, USD per million tokens.</summary>
    public const decimal MaxRatePerMTok = 1000m;

    private static readonly HashSet<string> Providers = new(StringComparer.Ordinal) { "anthropic", "openai" };
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "chat", "responses" };

    private static readonly string[] KeptFields =
    [
        "litellm_provider",
        "mode",
        "input_cost_per_token",
        "output_cost_per_token",
        "cache_read_input_token_cost",
        "cache_creation_input_token_cost",
        "cache_creation_input_token_cost_above_1hr"
    ];

    /// <summary>
    /// Parses a catalog document into a pricing table. Returns null when the
    /// text is not a JSON object.
    /// </summary>
    public static ModelPricingTable? TryParse(string json)
    {
        using var document = TryParseDocument(json);
        return document is null ? null : Parse(document.RootElement);
    }

    /// <summary>Parses an already-loaded catalog object into a pricing table.</summary>
    public static ModelPricingTable Parse(JsonElement catalog)
    {
        var entries = new List<KeyValuePair<string, ModelPrice>>();
        foreach (var (model, entry) in UsableEntries(catalog))
        {
            var cacheWrite = ReadRate(entry, "cache_creation_input_token_cost");
            entries.Add(new KeyValuePair<string, ModelPrice>(model, new ModelPrice
            {
                InputPerMTok = ReadRate(entry, "input_cost_per_token"),
                CachedInputPerMTok = ReadRate(entry, "cache_read_input_token_cost"),
                CacheWrite5mPerMTok = cacheWrite,
                CacheWrite1hPerMTok = ReadRate(entry, "cache_creation_input_token_cost_above_1hr") ?? cacheWrite,
                OutputPerMTok = ReadRate(entry, "output_cost_per_token")
            }));
        }

        return new ModelPricingTable(entries);
    }

    /// <summary>
    /// Cuts a full catalog down to the entries and fields this app reads, in
    /// the catalog's own shape. Returns null when the text is not a JSON object
    /// or holds fewer than <see cref="MinimumModels"/> usable models.
    /// </summary>
    public static JsonObject? TryTrim(string json)
    {
        using var document = TryParseDocument(json);
        if (document is null)
        {
            return null;
        }

        var trimmed = new JsonObject();
        foreach (var (model, entry) in UsableEntries(document.RootElement))
        {
            var kept = new JsonObject();
            foreach (var field in KeptFields)
            {
                if (entry.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    kept[field] = JsonNode.Parse(value.GetRawText());
                }
            }

            trimmed[model] = kept;
        }

        return trimmed.Count >= MinimumModels ? trimmed : null;
    }

    private static JsonDocument? TryParseDocument(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<(string Model, JsonElement Entry)> UsableEntries(JsonElement catalog)
    {
        if (catalog.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in catalog.EnumerateObject())
        {
            var entry = property.Value;
            if (string.IsNullOrWhiteSpace(property.Name) ||
                entry.ValueKind != JsonValueKind.Object ||
                !Providers.Contains(ReadString(entry, "litellm_provider") ?? string.Empty) ||
                !Modes.Contains(ReadString(entry, "mode") ?? string.Empty) ||
                ReadRate(entry, "input_cost_per_token") is null ||
                ReadRate(entry, "output_cost_per_token") is null)
            {
                continue;
            }

            yield return (property.Name, entry);
        }
    }

    private static string? ReadString(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? ReadRate(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        decimal perToken;
        if (!value.TryGetDecimal(out perToken))
        {
            if (!value.TryGetDouble(out var asDouble) || !double.IsFinite(asDouble) || Math.Abs(asDouble) > 1)
            {
                return null;
            }

            perToken = (decimal)asDouble;
        }

        var perMTok = Math.Round(perToken * UsageCostCalculator.TokensPerMillion, 6);
        return perMTok is >= 0m and <= MaxRatePerMTok ? perMTok : null;
    }
}
