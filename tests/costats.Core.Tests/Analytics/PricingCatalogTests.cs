using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using costats.Core.Analytics;
using costats.Infrastructure.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

public sealed class PricingCatalogTests : IDisposable
{
    private static readonly Uri CatalogUrl = new("https://catalog.test/prices.json");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "costats-pricing-" + Guid.NewGuid().ToString("N"));

    public PricingCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // -- Catalog parsing ---------------------------------------------------

    [Fact]
    public void Per_token_rates_become_per_million_token_rates()
    {
        var table = LiteLlmPricingCatalog.TryParse("""
            {
              "claude-new": {
                "litellm_provider": "anthropic", "mode": "chat",
                "input_cost_per_token": 4e-6, "output_cost_per_token": 0.00002,
                "cache_read_input_token_cost": 2e-7,
                "cache_creation_input_token_cost": 0.000005,
                "cache_creation_input_token_cost_above_1hr": 0.000008
              }
            }
            """)!;

        var price = table.Find("claude-new");
        Assert.Equal(4m, price.InputPerMTok);
        Assert.Equal(20m, price.OutputPerMTok);
        Assert.Equal(0.2m, price.CachedInputPerMTok);
        Assert.Equal(5m, price.CacheWrite5mPerMTok);
        Assert.Equal(8m, price.CacheWrite1hPerMTok);
    }

    [Fact]
    public void A_single_cache_write_rate_prices_both_ttl_buckets()
    {
        var table = LiteLlmPricingCatalog.TryParse("""
            { "gpt-new": { "litellm_provider": "openai", "mode": "responses",
              "input_cost_per_token": 0.000002, "output_cost_per_token": 0.000012,
              "cache_creation_input_token_cost": 0.0000025 } }
            """)!;

        var price = table.Find("gpt-new");
        Assert.Equal(2.5m, price.CacheWrite5mPerMTok);
        Assert.Equal(2.5m, price.CacheWrite1hPerMTok);
        Assert.Null(price.CachedInputPerMTok);
    }

    [Fact]
    public void Partner_hosted_and_non_chat_entries_are_ignored()
    {
        // The Bedrock id normalises onto the first-party one, so reading it
        // would replace the first-party price with the partner's.
        var table = LiteLlmPricingCatalog.TryParse("""
            {
              "claude-x": { "litellm_provider": "anthropic", "mode": "chat", "input_cost_per_token": 0.000005, "output_cost_per_token": 0.000025 },
              "anthropic.claude-x": { "litellm_provider": "bedrock", "mode": "chat", "input_cost_per_token": 0.000009, "output_cost_per_token": 0.00009 },
              "gpt-image": { "litellm_provider": "openai", "mode": "image_generation", "input_cost_per_token": 0.000005, "output_cost_per_token": 0.00004 },
              "sample_spec": { "litellm_provider": "one of https://docs.litellm.ai/docs/providers", "mode": "chat" }
            }
            """)!;

        Assert.Equal(5m, table.Find("claude-x").InputPerMTok);
        Assert.Single(table.Entries);
    }

    [Theory]
    [InlineData("\"input_cost_per_token\": 0.000005")]
    [InlineData("\"input_cost_per_token\": 0.000005, \"output_cost_per_token\": -0.00001")]
    [InlineData("\"input_cost_per_token\": 0.000005, \"output_cost_per_token\": 0.5")]
    [InlineData("\"input_cost_per_token\": \"0.000005\", \"output_cost_per_token\": 0.00002")]
    public void An_entry_without_two_sane_headline_rates_is_skipped(string rates)
    {
        var table = LiteLlmPricingCatalog.TryParse(
            "{ \"m\": { \"litellm_provider\": \"openai\", \"mode\": \"chat\", " + rates + " } }")!;

        Assert.Empty(table.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void Text_that_is_not_a_catalog_object_does_not_parse(string json)
    {
        Assert.Null(LiteLlmPricingCatalog.TryParse(json));
    }

    [Fact]
    public void Trimming_keeps_only_usable_entries_and_the_fields_the_app_reads()
    {
        var trimmed = LiteLlmPricingCatalog.TryTrim(Catalog(25, extra: """
            "bedrock-only": { "litellm_provider": "bedrock", "mode": "chat", "input_cost_per_token": 1e-6, "output_cost_per_token": 1e-6 },
            """))!;

        Assert.Equal(25, trimmed.Count);
        var entry = trimmed["model-0"]!.AsObject();
        Assert.False(entry.ContainsKey("max_tokens"));
        Assert.True(entry.ContainsKey("input_cost_per_token"));
        Assert.Equal(25, LiteLlmPricingCatalog.TryParse(trimmed.ToJsonString())!.Entries.Count);
    }

    [Fact]
    public void Trimming_rejects_a_catalog_with_too_few_models()
    {
        Assert.Null(LiteLlmPricingCatalog.TryTrim(Catalog(LiteLlmPricingCatalog.MinimumModels - 1)));
    }

    // -- Layering ----------------------------------------------------------

    [Fact]
    public void Catalog_beats_the_snapshot_and_the_user_file_beats_both()
    {
        var catalog = new ModelPricingTable(
        [
            new KeyValuePair<string, ModelPrice>("claude-opus-5", Price(7m)),
            new KeyValuePair<string, ModelPrice>("gpt-5.6-sol", Price(9m)),
            new KeyValuePair<string, ModelPrice>("brand-new", Price(1m))
        ]);
        var overridePath = Path.Combine(_root, "pricing.json");
        File.WriteAllText(overridePath, """{ "gpt-5.6-sol": { "input": 3, "output": 30 } }""");

        var table = ModelPricingLoader.Load(overridePath, catalog);

        Assert.Equal(7m, table.Find("claude-opus-5").InputPerMTok);
        Assert.Equal(3m, table.Find("gpt-5.6-sol").InputPerMTok);
        Assert.Equal(1m, table.Find("brand-new").InputPerMTok);
        Assert.Equal(10m, table.Find("claude-fable-5").InputPerMTok);
    }

    [Fact]
    public void A_catalog_cannot_price_a_pinned_model_but_the_user_file_can()
    {
        var catalog = new ModelPricingTable([new KeyValuePair<string, ModelPrice>("codex-auto-review", Price(1m))]);
        var missing = Path.Combine(_root, "none.json");
        var overridePath = Path.Combine(_root, "pricing.json");
        File.WriteAllText(overridePath, """{ "codex-auto-review": { "input": 0.2, "output": 1.2 } }""");

        Assert.False(ModelPricingLoader.Load(missing, catalog).IsPriced("codex-auto-review"));
        Assert.Equal(0.2m, ModelPricingLoader.Load(overridePath, catalog).Find("codex-auto-review").InputPerMTok);
    }

    // -- Updater -----------------------------------------------------------

    [Fact]
    public async Task A_first_refresh_downloads_and_caches_the_catalog()
    {
        var handler = new FakeHandler(_ => Ok(Catalog(25), "\"v1\""));
        var updater = Updater(handler, new FakeTime());

        await updater.RefreshIfStaleAsync();

        Assert.Equal(1, handler.Calls);
        Assert.Equal(25, updater.ReadCached()!.Entries.Count);
        var cache = JsonNode.Parse(File.ReadAllText(updater.CachePath))!;
        Assert.Equal("\"v1\"", (string?)cache["etag"]);
        Assert.Equal(CatalogUrl.AbsoluteUri, (string?)cache["source"]);
    }

    [Fact]
    public async Task A_fresh_cache_is_not_downloaded_again()
    {
        var time = new FakeTime();
        var handler = new FakeHandler(_ => Ok(Catalog(25), "\"v1\""));
        var updater = Updater(handler, time);

        await updater.RefreshIfStaleAsync();
        time.Advance(TimeSpan.FromHours(23));
        await updater.RefreshIfStaleAsync();

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_stale_cache_is_revalidated_with_its_etag_and_kept_on_304()
    {
        var time = new FakeTime();
        var handler = new FakeHandler(request => request.Headers.IfNoneMatch.Count == 0
            ? Ok(Catalog(25), "\"v1\"")
            : new HttpResponseMessage(HttpStatusCode.NotModified));
        var updater = Updater(handler, time);

        await updater.RefreshIfStaleAsync();
        time.Advance(TimeSpan.FromHours(25));
        await updater.RefreshIfStaleAsync();

        Assert.Equal(2, handler.Calls);
        Assert.Equal("\"v1\"", handler.LastIfNoneMatch);
        Assert.Equal(25, updater.ReadCached()!.Entries.Count);

        // The 304 counts as a fresh check.
        time.Advance(TimeSpan.FromHours(1));
        await updater.RefreshIfStaleAsync();
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task An_unusable_download_keeps_the_previous_prices()
    {
        var time = new FakeTime();
        var responses = new Queue<HttpResponseMessage>([Ok(Catalog(25), "\"v1\""), Ok("""{ "changed": "shape" }""", "\"v2\"")]);
        var handler = new FakeHandler(_ => responses.Dequeue());
        var updater = Updater(handler, time);

        await updater.RefreshIfStaleAsync();
        time.Advance(TimeSpan.FromHours(25));
        await updater.RefreshIfStaleAsync();

        Assert.Equal(25, updater.ReadCached()!.Entries.Count);
    }

    [Fact]
    public async Task A_network_failure_falls_back_quietly_and_waits_before_retrying()
    {
        var time = new FakeTime();
        var handler = new FakeHandler(_ => throw new HttpRequestException("offline"));
        var updater = Updater(handler, time);

        await updater.RefreshIfStaleAsync();
        await updater.RefreshIfStaleAsync();
        Assert.Equal(1, handler.Calls);
        Assert.Null(updater.ReadCached());

        time.Advance(PricingCatalogUpdater.RetryAfterFailure);
        await updater.RefreshIfStaleAsync();
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task The_provider_prices_with_the_downloaded_catalog()
    {
        var handler = new FakeHandler(_ => Ok(Catalog(25), "\"v1\""));
        var provider = new ModelPricingProvider(Updater(handler, new FakeTime()), Path.Combine(_root, "none.json"));

        var table = await provider.GetAsync();

        Assert.Equal(1m, table.Find("model-1").InputPerMTok);
        Assert.Equal(5m, table.Find("claude-opus-5").InputPerMTok);
    }

    [Fact]
    public async Task The_provider_picks_up_an_edited_override_file()
    {
        var overridePath = Path.Combine(_root, "pricing.json");
        var provider = new ModelPricingProvider(updater: null, overridePath);
        Assert.False((await provider.GetAsync()).IsPriced("my-model"));

        File.WriteAllText(overridePath, """{ "my-model": { "input": 1, "output": 2 } }""");
        File.SetLastWriteTimeUtc(overridePath, DateTime.UtcNow.AddMinutes(1));

        Assert.True((await provider.GetAsync()).IsPriced("my-model"));
    }

    private PricingCatalogUpdater Updater(FakeHandler handler, FakeTime time) => new(
        new PricingCatalogOptions { CatalogUrl = CatalogUrl, RefreshInterval = TimeSpan.FromHours(24) },
        Path.Combine(_root, "pricing-catalog.json"),
        handler,
        time);

    private static ModelPrice Price(decimal input) => new() { InputPerMTok = input, OutputPerMTok = input * 5 };

    // model-N costs $N per MTok input; each entry carries a field the trim drops.
    private static string Catalog(int models, string extra = "")
    {
        var builder = new StringBuilder("{").Append(extra);
        for (var i = 0; i < models; i++)
        {
            builder.Append(i == 0 ? string.Empty : ",")
                .Append($"\"model-{i}\": {{ \"litellm_provider\": \"openai\", \"mode\": \"chat\", \"max_tokens\": 1000, ")
                .Append($"\"input_cost_per_token\": {i}e-6, \"output_cost_per_token\": 0.00001 }}");
        }

        return builder.Append('}').ToString();
    }

    private static HttpResponseMessage Ok(string body, string etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string? LastIfNoneMatch { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastIfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
