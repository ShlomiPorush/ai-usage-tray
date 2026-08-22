using System.Globalization;
using System.Text.Json;
using costats.Core.Analytics;

namespace costats.Infrastructure.Analytics;

/// <summary>
/// One usage record read out of a log file, before global deduplication and
/// before it is attributed to an account.
/// </summary>
/// <param name="DedupKey">
/// Stable 64-bit hash of the provider's own request identity, or 0 when the
/// entry carries no identity and therefore cannot be a duplicate.
/// </param>
/// <param name="Timestamp">UTC timestamp from the log line.</param>
/// <param name="Model">Model id exactly as logged.</param>
/// <param name="Tokens">Normalised token counts.</param>
public readonly record struct RawUsageEntry(
    long DedupKey,
    DateTimeOffset Timestamp,
    string Model,
    UsageTokens Tokens);

/// <summary>The usage found in a single log file.</summary>
/// <param name="Entries">Usage records, in file order.</param>
/// <param name="SkippedLines">Lines that looked like usage but did not parse.</param>
public sealed record ParsedUsageFile(IReadOnlyList<RawUsageEntry> Entries, int SkippedLines)
{
    /// <summary>A file with no usage in it.</summary>
    public static readonly ParsedUsageFile Empty = new([], 0);
}

/// <summary>
/// Reads token usage out of Claude Code and Codex JSONL logs.
/// </summary>
/// <remarks>
/// Privacy: only the usage object, the model id, the timestamp and the two
/// identity strings used for deduplication are ever pulled out of a line. The
/// line itself is a transient string needed to parse JSON and is dropped
/// immediately; no message content is retained, persisted or logged.
/// </remarks>
public static class UsageLogParser
{
    /// <summary>
    /// Bump when the parse output shape or semantics change, so stale cache
    /// entries written by an older build are ignored.
    /// </summary>
    public const int FormatVersion = 1;

    private const int ReadBufferSize = 128 * 1024;

    /// <summary>Longest line the parser will materialise; longer lines are skipped.</summary>
    public const int MaxLineLength = 8 * 1024 * 1024;

    /// <summary>
    /// Parses a Claude Code session file.
    /// <para>
    /// Usage lives on <c>message.usage</c> of <c>type: "assistant"</c> entries.
    /// Claude splits the input side into <c>input_tokens</c> (uncached),
    /// <c>cache_read_input_tokens</c> and <c>cache_creation_input_tokens</c>,
    /// with the creation side broken down by TTL under <c>cache_creation</c>.
    /// </para>
    /// <para>
    /// The TTL breakdown and the flat creation count disagree on a small number
    /// of real entries (either can be zero while the other is not), so the
    /// parser takes <c>max(cache_creation_input_tokens, 5m + 1h)</c> as the true
    /// creation total, charges the explicit 1-hour part at the 1-hour rate and
    /// puts the remainder on the cheaper 5-minute rate. That keeps the buckets
    /// summing to the total the provider reported and never invents 1-hour
    /// writes.
    /// </para>
    /// <para>
    /// The dedup key is <c>message.id</c> + <c>requestId</c>: Claude Code copies
    /// prior turns into every resumed or continued session file, so the same
    /// request appears in several files and must be counted once.
    /// </para>
    /// </summary>
    public static ParsedUsageFile ParseClaudeFile(string path, CancellationToken cancellationToken = default)
    {
        var entries = new List<RawUsageEntry>();
        var skipped = 0;

        foreach (var line in ReadLines(path, cancellationToken))
        {
            if (line.Length == 0 || !line.Contains("\"usage\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!root.TryGetProperty("message", out var message) ||
                    message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("usage", out var usage) ||
                    usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryReadTimestamp(root, out var timestamp))
                {
                    skipped++;
                    continue;
                }

                var model = ReadString(message, "model");
                if (string.IsNullOrEmpty(model) || IsSyntheticModel(model))
                {
                    // "<synthetic>" entries are Claude Code's own local
                    // bookkeeping, always zero tokens and never billed.
                    continue;
                }

                var uncachedInput = ReadLong(usage, "input_tokens");
                var cacheRead = ReadLong(usage, "cache_read_input_tokens");
                var cacheCreate = ReadLong(usage, "cache_creation_input_tokens");
                var output = ReadLong(usage, "output_tokens");

                long write1h = 0;
                long write5m = 0;
                if (usage.TryGetProperty("cache_creation", out var creation) &&
                    creation.ValueKind == JsonValueKind.Object)
                {
                    write1h = ReadLong(creation, "ephemeral_1h_input_tokens");
                    write5m = ReadLong(creation, "ephemeral_5m_input_tokens");
                }

                var creationTotal = Math.Max(cacheCreate, write5m + write1h);
                write1h = Math.Min(write1h, creationTotal);
                write5m = creationTotal - write1h;

                long thinking = 0;
                if (usage.TryGetProperty("output_tokens_details", out var outputDetails) &&
                    outputDetails.ValueKind == JsonValueKind.Object)
                {
                    thinking = ReadLong(outputDetails, "thinking_tokens");
                }

                var tokens = new UsageTokens
                {
                    UncachedInputTokens = uncachedInput,
                    CacheReadInputTokens = cacheRead,
                    CacheWrite5mInputTokens = write5m,
                    CacheWrite1hInputTokens = write1h,
                    OutputTokens = output,
                    ReasoningOutputTokens = Math.Min(thinking, output)
                };

                if (tokens.ProcessedTokens == 0)
                {
                    continue;
                }

                var dedupKey = StableKey(ReadString(message, "id"), ReadString(root, "requestId"));
                entries.Add(new RawUsageEntry(dedupKey, timestamp, model, tokens));
            }
            catch (JsonException)
            {
                skipped++;
            }
        }

        return entries.Count == 0 && skipped == 0
            ? ParsedUsageFile.Empty
            : new ParsedUsageFile(entries, skipped);
    }

    /// <summary>
    /// Parses a Codex rollout file.
    /// <para>
    /// Usage lives on <c>event_msg</c> entries whose
    /// <c>payload.type</c> is <c>token_count</c>, in
    /// <c>payload.info.last_token_usage</c>, which is already the delta for that
    /// turn: the events are summed directly, never differenced.
    /// <c>total_tokens</c> is ignored because it disagrees with the component
    /// sum on a small number of context-only events.
    /// </para>
    /// <para>
    /// Codex counts the cached part inside <c>input_tokens</c>, so the uncached
    /// bucket is <c>input_tokens - cached_input_tokens</c>. Cache writes are
    /// reported flat in <c>cache_write_input_tokens</c> with no TTL, and are
    /// recorded as 5-minute writes.
    /// </para>
    /// <para>
    /// The model for a turn comes from the most recent <c>turn_context</c>
    /// record in the same file (<c>payload.model</c>). Rollout files are unique
    /// per session, so there is nothing to deduplicate and the key is always 0.
    /// </para>
    /// </summary>
    public static ParsedUsageFile ParseCodexFile(string path, CancellationToken cancellationToken = default)
    {
        var entries = new List<RawUsageEntry>();
        var skipped = 0;
        var currentModel = string.Empty;

        foreach (var line in ReadLines(path, cancellationToken))
        {
            var isTurnContext = line.Contains("\"turn_context\"", StringComparison.Ordinal);
            var isTokenCount = line.Contains("\"token_count\"", StringComparison.Ordinal);
            if (!isTurnContext && !isTokenCount)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = ReadString(root, "type");

                if (string.Equals(type, "turn_context", StringComparison.Ordinal))
                {
                    var model = ReadString(payload, "model");
                    if (!string.IsNullOrEmpty(model))
                    {
                        currentModel = model;
                    }

                    continue;
                }

                if (!string.Equals(type, "event_msg", StringComparison.Ordinal) ||
                    !string.Equals(ReadString(payload, "type"), "token_count", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!payload.TryGetProperty("info", out var info) ||
                    info.ValueKind != JsonValueKind.Object ||
                    !info.TryGetProperty("last_token_usage", out var last) ||
                    last.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryReadTimestamp(root, out var timestamp))
                {
                    skipped++;
                    continue;
                }

                var input = ReadLong(last, "input_tokens");
                var cached = Math.Min(ReadLong(last, "cached_input_tokens"), input);
                var cacheWrite = ReadLong(last, "cache_write_input_tokens");
                var output = ReadLong(last, "output_tokens");
                var reasoning = ReadLong(last, "reasoning_output_tokens");

                var tokens = new UsageTokens
                {
                    UncachedInputTokens = input - cached,
                    CacheReadInputTokens = cached,
                    CacheWrite5mInputTokens = cacheWrite,
                    CacheWrite1hInputTokens = 0,
                    OutputTokens = output,
                    ReasoningOutputTokens = Math.Min(reasoning, output)
                };

                if (tokens.ProcessedTokens == 0)
                {
                    continue;
                }

                entries.Add(new RawUsageEntry(
                    0,
                    timestamp,
                    string.IsNullOrEmpty(currentModel) ? "unknown" : currentModel,
                    tokens));
            }
            catch (JsonException)
            {
                skipped++;
            }
        }

        return entries.Count == 0 && skipped == 0
            ? ParsedUsageFile.Empty
            : new ParsedUsageFile(entries, skipped);
    }

    /// <summary>
    /// FNV-1a 64 of <c>first|second</c>, or 0 when either part is missing.
    /// Deterministic across processes and builds, unlike
    /// <see cref="string.GetHashCode()"/>, which matters because the key is
    /// written into the on-disk parse cache.
    /// </summary>
    public static long StableKey(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second))
        {
            return 0;
        }

        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        Mix(ref hash, first);
        hash = (hash ^ (byte)'|') * prime;
        Mix(ref hash, second);

        // 0 is reserved for "no identity"; nudge the astronomically rare collision.
        return hash == 0 ? 1 : unchecked((long)hash);

        static void Mix(ref ulong hash, string value)
        {
            foreach (var character in value)
            {
                hash = (hash ^ (byte)(character & 0xFF)) * prime;
                hash = (hash ^ (byte)(character >> 8)) * prime;
            }
        }
    }

    private static bool IsSyntheticModel(string model) =>
        string.Equals(model, "<synthetic>", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ReadLines(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = ReadBufferSize
        });
        using var reader = new StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = reader.ReadLine();
            if (line is null)
            {
                yield break;
            }

            yield return line.Length > MaxLineLength ? string.Empty : line;
        }
    }

    private static bool TryReadTimestamp(JsonElement element, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!element.TryGetProperty("timestamp", out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        return !string.IsNullOrWhiteSpace(text) &&
            DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return value.TryGetInt64(out var number) ? Math.Max(0, number) : 0;
    }
}
