using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using costats.Core.Analytics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace costats.Infrastructure.Analytics;

/// <summary>Where and how often the live price catalog is fetched.</summary>
public sealed record PricingCatalogOptions
{
    /// <summary>The LiteLLM model-prices catalog on GitHub.</summary>
    public const string DefaultCatalogUrl =
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";

    /// <summary>Catalog to download. Null disables the download.</summary>
    public Uri? CatalogUrl { get; init; } = new(DefaultCatalogUrl);

    /// <summary>How long a downloaded catalog is used before it is checked again.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Keeps a local copy of the live price catalog at
/// <c>%LOCALAPPDATA%\costats\pricing-catalog.json</c>, checked at most once per
/// <see cref="PricingCatalogOptions.RefreshInterval"/>.
/// </summary>
/// <remarks>
/// <para>
/// The request is a plain GET of a public file: nothing about the user or
/// their usage is sent. A conditional request (<c>If-None-Match</c>) makes
/// the daily check a bodyless 304 while the catalog is unchanged.
/// </para>
/// <para>
/// A download is only accepted when it parses and holds at least
/// <see cref="LiteLlmPricingCatalog.MinimumModels"/> usable models; anything
/// else leaves the previous copy in place. A failed attempt is retried after
/// <see cref="RetryAfterFailure"/> rather than on every report.
/// </para>
/// </remarks>
public sealed class PricingCatalogUpdater
{
    /// <summary>Wait before retrying after a network failure.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly PricingCatalogOptions _options;
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private Task? _inflight;
    private DateTimeOffset _nextAttemptAfter = DateTimeOffset.MinValue;

    /// <summary>Creates an updater that writes to <paramref name="cachePath"/>.</summary>
    public PricingCatalogUpdater(
        PricingCatalogOptions options,
        string? cachePath = null,
        HttpMessageHandler? handler = null,
        TimeProvider? time = null,
        ILogger<PricingCatalogUpdater>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _cachePath = cachePath ?? DefaultCachePath();
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AIUsageTray-pricing");
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<PricingCatalogUpdater>.Instance;
    }

    /// <summary>Default location of the cached catalog.</summary>
    public static string DefaultCachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "costats",
        "pricing-catalog.json");

    /// <summary>The cache file this updater reads and writes.</summary>
    public string CachePath => _cachePath;

    /// <summary>
    /// The cached catalog as a pricing table, or null when there is no usable
    /// copy on disk.
    /// </summary>
    public ModelPricingTable? ReadCached()
    {
        var cached = ReadCacheFile();
        return cached?.Models is null ? null : LiteLlmPricingCatalog.Parse(cached.Models.Value);
    }

    /// <summary>
    /// Downloads the catalog when the cached copy is missing or older than the
    /// refresh interval. Concurrent callers share one download. Never throws:
    /// a failure is logged and the cached copy stays in use.
    /// </summary>
    public Task RefreshIfStaleAsync()
    {
        lock (_gate)
        {
            if (_inflight is { IsCompleted: false })
            {
                return _inflight;
            }

            var now = _time.GetUtcNow();
            if (_options.CatalogUrl is null || now < _nextAttemptAfter || IsFresh(now))
            {
                return Task.CompletedTask;
            }

            _inflight = Task.Run(RefreshAsync);
            return _inflight;
        }
    }

    private bool IsFresh(DateTimeOffset now)
    {
        var cached = ReadCacheFile();
        return cached?.Models is not null &&
               string.Equals(cached.Source, _options.CatalogUrl!.AbsoluteUri, StringComparison.Ordinal) &&
               now - cached.CheckedAt < _options.RefreshInterval;
    }

    private async Task RefreshAsync()
    {
        var url = _options.CatalogUrl!;
        var cached = ReadCacheFile();
        var sameSource = cached?.Models is not null && string.Equals(cached.Source, url.AbsoluteUri, StringComparison.Ordinal);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (sameSource && !string.IsNullOrEmpty(cached!.ETag) &&
                EntityTagHeaderValue.TryParse(cached.ETag, out var etag))
            {
                request.Headers.IfNoneMatch.Add(etag);
            }

            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && sameSource)
            {
                WriteCacheFile(url, cached!.ETag, JsonNode.Parse(cached.Models!.Value.GetRawText())!.AsObject());
                _logger.LogInformation("Price catalog unchanged since the last check");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Price catalog download failed: HTTP {Status}", (int)response.StatusCode);
                _nextAttemptAfter = _time.GetUtcNow() + RetryAfterFailure;
                return;
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var trimmed = LiteLlmPricingCatalog.TryTrim(body);
            if (trimmed is null)
            {
                // Not a network blip: the file itself is unusable. Wait a full
                // interval instead of re-downloading it every hour.
                _logger.LogWarning("Price catalog download was not a usable catalog; keeping the previous prices");
                _nextAttemptAfter = _time.GetUtcNow() + _options.RefreshInterval;
                return;
            }

            WriteCacheFile(url, response.Headers.ETag?.ToString(), trimmed);
            _logger.LogInformation("Price catalog updated: {Models} models", trimmed.Count);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Price catalog download failed: {Message}", exception.Message);
            _nextAttemptAfter = _time.GetUtcNow() + RetryAfterFailure;
        }
    }

    private CacheFile? ReadCacheFile()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_cachePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new CacheFile(
                ReadString(root, "source"),
                ReadString(root, "etag"),
                root.TryGetProperty("checkedAt", out var checkedAt) && checkedAt.TryGetDateTimeOffset(out var at) ? at : DateTimeOffset.MinValue,
                root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object ? models.Clone() : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteCacheFile(Uri source, string? etag, JsonObject models)
    {
        var document = new JsonObject
        {
            ["source"] = source.AbsoluteUri,
            ["etag"] = etag,
            ["checkedAt"] = _time.GetUtcNow().ToString("O"),
            ["models"] = models
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var temp = _cachePath + ".tmp";
        File.WriteAllText(temp, document.ToJsonString(WriteOptions));
        File.Move(temp, _cachePath, overwrite: true);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record CacheFile(string? Source, string? ETag, DateTimeOffset CheckedAt, JsonElement? Models);
}
