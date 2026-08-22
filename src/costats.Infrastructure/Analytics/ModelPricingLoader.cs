using System.Text.Json;
using System.Text.Json.Serialization;
using costats.Core.Analytics;

namespace costats.Infrastructure.Analytics;

/// <summary>
/// Loads the pricing table: the built-in defaults, optionally overlaid with a
/// user file at <c>%LOCALAPPDATA%\costats\pricing.json</c>.
/// </summary>
/// <remarks>
/// The override file exists so a model released after this build can be priced
/// without waiting for a release, and so a model the defaults deliberately
/// leave unpriced (such as <c>codex-auto-review</c>) can be given the rates the
/// user knows it resolves to. Its shape is a flat map of model id to rates in
/// USD per million tokens; omit a rate to leave it unknown:
/// <code>
/// {
///   "codex-auto-review": { "input": 0.2, "cachedInput": 0.02, "cacheWrite5m": 0.25, "output": 1.2 },
///   "claude-opus-5":     { "input": 5, "cachedInput": 0.5, "cacheWrite5m": 6.25, "cacheWrite1h": 10, "output": 25 }
/// }
/// </code>
/// A malformed file is ignored rather than allowed to break the report.
/// </remarks>
public static class ModelPricingLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Default location of the user override file.</summary>
    public static string DefaultOverridePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "costats",
        "pricing.json");

    /// <summary>
    /// Returns <see cref="ModelPricingTable.Default"/> merged with the override
    /// file when one is present and readable, and the plain defaults otherwise.
    /// </summary>
    public static ModelPricingTable Load(string? overridePath = null)
    {
        var path = overridePath ?? DefaultOverridePath();
        var overrides = TryReadOverrides(path);
        return overrides is null ? ModelPricingTable.Default : ModelPricingTable.Default.MergedWith(overrides);
    }

    /// <summary>
    /// Parses an override document. Returns null when the file is missing,
    /// unreadable or not valid JSON.
    /// </summary>
    public static ModelPricingTable? TryReadOverrides(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<Dictionary<string, PricingEntry?>>(stream, Options);
            return document is null ? null : FromEntries(document);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Parses an override document from a JSON string.</summary>
    public static ModelPricingTable? TryParseOverrides(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<Dictionary<string, PricingEntry?>>(json, Options);
            return document is null ? null : FromEntries(document);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ModelPricingTable FromEntries(Dictionary<string, PricingEntry?> document)
    {
        var entries = new List<KeyValuePair<string, ModelPrice>>(document.Count);
        foreach (var (model, entry) in document)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            entries.Add(new KeyValuePair<string, ModelPrice>(model, entry is null
                ? ModelPrice.Unpriced
                : new ModelPrice
                {
                    InputPerMTok = entry.Input,
                    CachedInputPerMTok = entry.CachedInput,
                    CacheWrite5mPerMTok = entry.CacheWrite5m,
                    CacheWrite1hPerMTok = entry.CacheWrite1h,
                    OutputPerMTok = entry.Output
                }));
        }

        return new ModelPricingTable(entries);
    }

    /// <summary>One model's rates as written in the override file.</summary>
    public sealed class PricingEntry
    {
        /// <summary>Full-rate input, USD per million tokens.</summary>
        [JsonPropertyName("input")]
        public decimal? Input { get; set; }

        /// <summary>Cache-read input, USD per million tokens.</summary>
        [JsonPropertyName("cachedInput")]
        public decimal? CachedInput { get; set; }

        /// <summary>5-minute cache write, USD per million tokens.</summary>
        [JsonPropertyName("cacheWrite5m")]
        public decimal? CacheWrite5m { get; set; }

        /// <summary>1-hour cache write, USD per million tokens.</summary>
        [JsonPropertyName("cacheWrite1h")]
        public decimal? CacheWrite1h { get; set; }

        /// <summary>Output, USD per million tokens.</summary>
        [JsonPropertyName("output")]
        public decimal? Output { get; set; }
    }
}
