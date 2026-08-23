using costats.Application.Settings;
using costats.Core.Analytics;
using costats.Infrastructure.Analytics;
using Xunit;

namespace costats.Core.Tests.Analytics;

/// <summary>
/// Fixture lines are trimmed copies of the real on-disk shapes: a Claude Code
/// <c>projects/**/*.jsonl</c> assistant entry and a Codex
/// <c>sessions/**/rollout-*.jsonl</c> event stream.
/// </summary>
public sealed class UsageLogParserTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "costats-usage-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private string WriteFile(string relativePath, params string[] lines)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    // Placeholders are @NAME@ rather than string interpolation because JSON's
    // trailing brace runs collide with raw interpolated string syntax.
    private const string ClaudeTemplate = """
        {"type":"assistant","timestamp":"@TS@","requestId":"@RID@","sessionId":"s1","uuid":"u1","message":{"id":"@MID@","role":"assistant","model":"@MODEL@","content":[{"type":"text","text":"redacted"}],"stop_reason":"end_turn","usage":{"input_tokens":@INPUT@,"cache_creation_input_tokens":@CC@,"cache_read_input_tokens":@CR@,"output_tokens":@OUT@,"output_tokens_details":{"thinking_tokens":@THINK@},"service_tier":"standard","cache_creation":{"ephemeral_1h_input_tokens":@E1H@,"ephemeral_5m_input_tokens":@E5M@}}}}
        """;

    private const string CodexTurnContextTemplate = """
        {"timestamp":"@TS@","type":"turn_context","payload":{"turn_id":"t1","cwd":"C:\\work","model":"@MODEL@","effort":"high"}}
        """;

    private const string CodexTokenCountTemplate = """
        {"timestamp":"@TS@","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":999999,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":999999,"reasoning_output_tokens":0,"total_tokens":999999},"last_token_usage":{"input_tokens":@INPUT@,"cached_input_tokens":@CACHED@,"cache_write_input_tokens":@CW@,"output_tokens":@OUT@,"reasoning_output_tokens":@REASON@,"total_tokens":@TOTAL@},"model_context_window":258400}}}
        """;

    private static string ClaudeLine(
        string messageId,
        string requestId,
        string timestamp,
        string model = "claude-opus-5",
        long input = 2,
        long cacheCreate = 9819,
        long cacheRead = 22450,
        long output = 290,
        long thinking = 89,
        long ephemeral5m = 0,
        long ephemeral1h = 9819) =>
        ClaudeTemplate
            .Replace("@TS@", timestamp, StringComparison.Ordinal)
            .Replace("@RID@", requestId, StringComparison.Ordinal)
            .Replace("@MID@", messageId, StringComparison.Ordinal)
            .Replace("@MODEL@", model, StringComparison.Ordinal)
            .Replace("@INPUT@", Number(input), StringComparison.Ordinal)
            .Replace("@CC@", Number(cacheCreate), StringComparison.Ordinal)
            .Replace("@CR@", Number(cacheRead), StringComparison.Ordinal)
            .Replace("@OUT@", Number(output), StringComparison.Ordinal)
            .Replace("@THINK@", Number(thinking), StringComparison.Ordinal)
            .Replace("@E1H@", Number(ephemeral1h), StringComparison.Ordinal)
            .Replace("@E5M@", Number(ephemeral5m), StringComparison.Ordinal);

    private static string CodexTurnContext(string model, string timestamp = "2026-08-12T16:07:02.724Z") =>
        CodexTurnContextTemplate
            .Replace("@TS@", timestamp, StringComparison.Ordinal)
            .Replace("@MODEL@", model, StringComparison.Ordinal);

    private static string CodexTokenCount(
        string timestamp,
        long input = 17247,
        long cached = 11008,
        long cacheWrite = 0,
        long output = 289,
        long reasoning = 136,
        long total = 17536) =>
        CodexTokenCountTemplate
            .Replace("@TS@", timestamp, StringComparison.Ordinal)
            .Replace("@INPUT@", Number(input), StringComparison.Ordinal)
            .Replace("@CACHED@", Number(cached), StringComparison.Ordinal)
            .Replace("@CW@", Number(cacheWrite), StringComparison.Ordinal)
            .Replace("@OUT@", Number(output), StringComparison.Ordinal)
            .Replace("@REASON@", Number(reasoning), StringComparison.Ordinal)
            .Replace("@TOTAL@", Number(total), StringComparison.Ordinal);

    private static string Number(long value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // -- Claude ------------------------------------------------------------

    [Fact]
    public void Claude_entry_maps_every_usage_field()
    {
        var path = WriteFile("a.jsonl", ClaudeLine("msg_1", "req_1", "2026-08-21T13:28:42.199Z"));

        var entry = Assert.Single(UsageLogParser.ParseClaudeFile(path).Entries);

        Assert.Equal("claude-opus-5", entry.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-08-21T13:28:42.199Z", System.Globalization.CultureInfo.InvariantCulture), entry.Timestamp);
        Assert.Equal(2, entry.Tokens.UncachedInputTokens);
        Assert.Equal(22450, entry.Tokens.CacheReadInputTokens);
        Assert.Equal(0, entry.Tokens.CacheWrite5mInputTokens);
        Assert.Equal(9819, entry.Tokens.CacheWrite1hInputTokens);
        Assert.Equal(290, entry.Tokens.OutputTokens);
        Assert.Equal(89, entry.Tokens.ReasoningOutputTokens);
        Assert.NotEqual(0, entry.DedupKey);
    }

    [Fact]
    public void Cache_creation_without_a_ttl_breakdown_is_charged_as_a_five_minute_write()
    {
        var path = WriteFile("a.jsonl", """
            {"type":"assistant","timestamp":"2026-08-21T13:00:00Z","requestId":"req_1","message":{"id":"msg_1","model":"claude-opus-5","usage":{"input_tokens":1,"cache_creation_input_tokens":5000,"cache_read_input_tokens":0,"output_tokens":10}}}
            """);

        var entry = Assert.Single(UsageLogParser.ParseClaudeFile(path).Entries);

        Assert.Equal(5000, entry.Tokens.CacheWrite5mInputTokens);
        Assert.Equal(0, entry.Tokens.CacheWrite1hInputTokens);
    }

    [Fact]
    public void A_ttl_breakdown_larger_than_the_flat_count_wins()
    {
        // Real logs contain entries where cache_creation_input_tokens is 0 while
        // the ephemeral breakdown is not; the breakdown is the truth there.
        var path = WriteFile("a.jsonl", ClaudeLine(
            "msg_1", "req_1", "2026-08-21T13:00:00Z", cacheCreate: 0, ephemeral1h: 4266, ephemeral5m: 0));

        var entry = Assert.Single(UsageLogParser.ParseClaudeFile(path).Entries);

        Assert.Equal(4266, entry.Tokens.CacheWrite1hInputTokens);
        Assert.Equal(0, entry.Tokens.CacheWrite5mInputTokens);
        Assert.Equal(4266, entry.Tokens.CacheWriteInputTokens);
    }

    [Fact]
    public void Cache_write_buckets_always_sum_to_the_reported_creation_total()
    {
        var path = WriteFile("a.jsonl", ClaudeLine(
            "msg_1", "req_1", "2026-08-21T13:00:00Z", cacheCreate: 10_000, ephemeral1h: 3_000, ephemeral5m: 0));

        var entry = Assert.Single(UsageLogParser.ParseClaudeFile(path).Entries);

        Assert.Equal(3_000, entry.Tokens.CacheWrite1hInputTokens);
        Assert.Equal(7_000, entry.Tokens.CacheWrite5mInputTokens);
    }

    [Fact]
    public void Synthetic_and_zero_token_entries_are_skipped()
    {
        var path = WriteFile("a.jsonl",
            ClaudeLine("msg_1", "req_1", "2026-08-21T13:00:00Z", model: "<synthetic>"),
            ClaudeLine("msg_2", "req_2", "2026-08-21T13:00:01Z", input: 0, cacheCreate: 0, cacheRead: 0, output: 0, thinking: 0, ephemeral1h: 0));

        Assert.Empty(UsageLogParser.ParseClaudeFile(path).Entries);
    }

    [Fact]
    public void A_corrupt_line_is_counted_and_the_rest_of_the_file_still_parses()
    {
        var path = WriteFile("a.jsonl",
            ClaudeLine("msg_1", "req_1", "2026-08-21T13:00:00Z"),
            """{"type":"assistant","usage":"this is not an object" """,
            "",
            "not json at all",
            ClaudeLine("msg_2", "req_2", "2026-08-21T13:00:01Z"));

        var parsed = UsageLogParser.ParseClaudeFile(path);

        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal(1, parsed.SkippedLines);
    }

    [Fact]
    public void An_entry_without_a_timestamp_is_skipped_not_dated_to_now()
    {
        var path = WriteFile("a.jsonl", """
            {"type":"assistant","requestId":"req_1","message":{"id":"msg_1","model":"claude-opus-5","usage":{"input_tokens":10,"output_tokens":10}}}
            """);

        var parsed = UsageLogParser.ParseClaudeFile(path);

        Assert.Empty(parsed.Entries);
        Assert.Equal(1, parsed.SkippedLines);
    }

    // -- Codex -------------------------------------------------------------

    [Fact]
    public void Codex_event_subtracts_the_cached_part_from_the_input_total()
    {
        var path = WriteFile("rollout-1.jsonl",
            CodexTurnContext("gpt-5.6-sol"),
            CodexTokenCount("2026-08-12T16:07:12.784Z"));

        var entry = Assert.Single(UsageLogParser.ParseCodexFile(path).Entries);

        Assert.Equal("gpt-5.6-sol", entry.Model);
        Assert.Equal(17247 - 11008, entry.Tokens.UncachedInputTokens);
        Assert.Equal(11008, entry.Tokens.CacheReadInputTokens);
        Assert.Equal(289, entry.Tokens.OutputTokens);
        Assert.Equal(136, entry.Tokens.ReasoningOutputTokens);
        Assert.Equal(17247 + 289, entry.Tokens.ProcessedTokens);
        Assert.Equal(0, entry.DedupKey);
    }

    [Fact]
    public void Codex_events_are_summed_not_differenced()
    {
        // last_token_usage is already the per-turn delta. total_token_usage in
        // the same record is deliberately ignored.
        var path = WriteFile("rollout-1.jsonl",
            CodexTurnContext("gpt-5.6-sol"),
            CodexTokenCount("2026-08-12T16:07:12Z", input: 100, cached: 40, output: 10, reasoning: 5),
            CodexTokenCount("2026-08-12T16:08:12Z", input: 300, cached: 200, output: 20, reasoning: 8));

        var entries = UsageLogParser.ParseCodexFile(path).Entries;

        Assert.Equal(2, entries.Count);
        Assert.Equal(160, entries.Sum(entry => entry.Tokens.UncachedInputTokens));
        Assert.Equal(240, entries.Sum(entry => entry.Tokens.CacheReadInputTokens));
        Assert.Equal(30, entries.Sum(entry => entry.Tokens.OutputTokens));
    }

    [Fact]
    public void Codex_model_follows_the_latest_turn_context()
    {
        var path = WriteFile("rollout-1.jsonl",
            CodexTurnContext("gpt-5.6-sol"),
            CodexTokenCount("2026-08-12T16:07:12Z", input: 10, cached: 0, output: 1),
            CodexTurnContext("codex-auto-review", "2026-08-12T16:09:00Z"),
            CodexTokenCount("2026-08-12T16:10:00Z", input: 20, cached: 0, output: 2));

        var entries = UsageLogParser.ParseCodexFile(path).Entries;

        Assert.Equal("gpt-5.6-sol", entries[0].Model);
        Assert.Equal("codex-auto-review", entries[1].Model);
    }

    [Fact]
    public void Codex_events_that_report_no_tokens_are_dropped()
    {
        var path = WriteFile("rollout-1.jsonl",
            CodexTurnContext("gpt-5.6-sol"),
            CodexTokenCount("2026-08-12T16:07:12Z", input: 0, cached: 0, cacheWrite: 0, output: 0, reasoning: 0, total: 231356));

        Assert.Empty(UsageLogParser.ParseCodexFile(path).Entries);
    }

    [Fact]
    public void Codex_cache_writes_land_in_the_five_minute_bucket()
    {
        var path = WriteFile("rollout-1.jsonl",
            CodexTurnContext("gpt-5"),
            CodexTokenCount("2026-08-12T16:07:12Z", input: 100, cached: 0, cacheWrite: 40, output: 5));

        var entry = Assert.Single(UsageLogParser.ParseCodexFile(path).Entries);

        Assert.Equal(40, entry.Tokens.CacheWrite5mInputTokens);
        Assert.Equal(0, entry.Tokens.CacheWrite1hInputTokens);
    }

    // -- Dedup -------------------------------------------------------------

    [Fact]
    public void The_dedup_key_is_stable_across_calls_and_distinguishes_requests()
    {
        Assert.Equal(
            UsageLogParser.StableKey("msg_1", "req_1"),
            UsageLogParser.StableKey("msg_1", "req_1"));
        Assert.NotEqual(
            UsageLogParser.StableKey("msg_1", "req_1"),
            UsageLogParser.StableKey("msg_1", "req_2"));
        Assert.NotEqual(
            UsageLogParser.StableKey("msg_1", "req_1"),
            UsageLogParser.StableKey("msg_2", "req_1"));
        // "a|b" and "a" + "|b" must not collide.
        Assert.NotEqual(
            UsageLogParser.StableKey("a|b", "c"),
            UsageLogParser.StableKey("a", "b|c"));
        Assert.Equal(0, UsageLogParser.StableKey("msg_1", null));
        Assert.Equal(0, UsageLogParser.StableKey(null, "req_1"));
    }

    [Fact]
    public async Task The_same_request_copied_into_a_resumed_session_file_is_counted_once()
    {
        // This is exactly what Claude Code does on resume: the new session file
        // replays earlier turns verbatim. Without global dedup the cost roughly
        // doubles.
        var shared = ClaudeLine("msg_shared", "req_shared", "2026-08-21T13:00:00Z", input: 100, cacheCreate: 0, cacheRead: 0, output: 50, thinking: 0, ephemeral1h: 0);
        WriteFile(Path.Combine("acct", "projects", "p1", "session-a.jsonl"),
            shared,
            ClaudeLine("msg_only_a", "req_only_a", "2026-08-21T13:01:00Z", input: 7, cacheCreate: 0, cacheRead: 0, output: 3, thinking: 0, ephemeral1h: 0));
        WriteFile(Path.Combine("acct", "projects", "p1", "session-b.jsonl"),
            shared,
            ClaudeLine("msg_only_b", "req_only_b", "2026-08-21T13:02:00Z", input: 1, cacheCreate: 0, cacheRead: 0, output: 1, thinking: 0, ephemeral1h: 0));

        var result = await Collect(Claude("claude-1", Path.Combine(_root, "acct")));

        Assert.Equal(3, result.Samples.Count);
        Assert.Equal(1, result.Diagnostics.DuplicatesDropped);
        Assert.Equal(150 + 10 + 2, result.Samples.Sum(sample => sample.Tokens.ProcessedTokens));
    }

    [Fact]
    public async Task Codex_entries_are_never_deduplicated_against_each_other()
    {
        // Identical turns in different sessions are genuinely separate spend.
        WriteFile(Path.Combine("codex", "sessions", "2026", "08", "12", "rollout-1.jsonl"),
            CodexTurnContext("gpt-5.6-sol"),
            CodexTokenCount("2026-08-12T16:07:12Z", input: 100, cached: 0, output: 10));
        WriteFile(Path.Combine("codex", "sessions", "2026", "08", "12", "rollout-2.jsonl"),
            CodexTurnContext("gpt-5.6-sol"),
            CodexTokenCount("2026-08-12T16:07:12Z", input: 100, cached: 0, output: 10));

        var result = await Collect(Codex("codex-1", Path.Combine(_root, "codex")));

        Assert.Equal(2, result.Samples.Count);
        Assert.Equal(0, result.Diagnostics.DuplicatesDropped);
        Assert.Equal(220, result.Samples.Sum(sample => sample.Tokens.ProcessedTokens));
    }

    [Fact]
    public async Task Files_that_are_not_codex_rollouts_are_ignored_under_a_codex_root()
    {
        WriteFile(Path.Combine("codex", "sessions", "rollout-1.jsonl"),
            CodexTurnContext("gpt-5.6-sol"),
            CodexTokenCount("2026-08-12T16:07:12Z", input: 100, cached: 0, output: 10));
        WriteFile(Path.Combine("codex", "sessions", "history.jsonl"),
            CodexTurnContext("gpt-5.6-sol"),
            CodexTokenCount("2026-08-12T16:07:12Z", input: 500, cached: 0, output: 50));

        var result = await Collect(Codex("codex-1", Path.Combine(_root, "codex")));

        Assert.Equal(110, Assert.Single(result.Samples).Tokens.ProcessedTokens);
    }

    // -- Roots -------------------------------------------------------------

    [Fact]
    public void Every_codex_account_lands_in_one_merged_bucket()
    {
        Directory.CreateDirectory(Path.Combine(_root, "codex-a", "sessions"));
        Directory.CreateDirectory(Path.Combine(_root, "codex-b", "sessions"));

        var roots = UsageLogRootResolver.Resolve(
        [
            Codex("codex-1", Path.Combine(_root, "codex-a")),
            Codex("codex-2", Path.Combine(_root, "codex-b"))
        ]);

        Assert.Equal(2, roots.Count);
        Assert.All(roots, root => Assert.Equal(UsageAccounts.MergedCodexId, root.AccountId));
        Assert.All(roots, root => Assert.Equal(UsageProviderKind.Codex, root.Provider));
    }

    [Fact]
    public void Accounts_sharing_a_directory_are_scanned_once()
    {
        // Four Codex profiles symlink their sessions folder to the same place on
        // the real machine. Resolving and deduplicating the paths is what stops
        // the same rollout files being counted once per account.
        Directory.CreateDirectory(Path.Combine(_root, "shared", "sessions"));

        var roots = UsageLogRootResolver.Resolve(
        [
            Codex("codex-1", Path.Combine(_root, "shared")),
            Codex("codex-2", Path.Combine(_root, "shared")),
            Codex("codex-3", Path.Combine(_root, "shared", "..", "shared"))
        ]);

        Assert.Equal(Path.Combine(_root, "shared", "sessions"), Assert.Single(roots).Path);
    }

    [Fact]
    public void A_missing_log_directory_is_not_a_root()
    {
        var roots = UsageLogRootResolver.Resolve([Claude("claude-1", Path.Combine(_root, "nope"))]);

        Assert.Empty(roots);
    }

    [Fact]
    public void Standard_codex_logs_are_discovered_when_monitored_profiles_are_credential_only()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".codex", "sessions"));
        var settings = new AppSettings
        {
            Accounts =
            [
                Codex("codex-isolated", Path.Combine(_root, ".codex-isolated"))
            ]
        };

        var roots = UsageLogRootResolver.Resolve(settings.GetLocalUsageAccounts(_root));

        var root = Assert.Single(roots);
        Assert.Equal(Path.Combine(_root, ".codex", "sessions"), root.Path);
        Assert.Equal(UsageAccounts.MergedCodexId, root.AccountId);
    }

    [Fact]
    public void Claude_accounts_keep_their_own_identity()
    {
        Directory.CreateDirectory(Path.Combine(_root, "c1", "projects"));
        Directory.CreateDirectory(Path.Combine(_root, "c2", "projects"));

        var roots = UsageLogRootResolver.Resolve(
        [
            Claude("claude-1", Path.Combine(_root, "c1")),
            Claude("claude-2", Path.Combine(_root, "c2"))
        ]);

        Assert.Equal(["claude-1", "claude-2"], roots.Select(root => root.AccountId).Order());
    }

    [Fact]
    public async Task Samples_carry_their_account_and_provider()
    {
        WriteFile(Path.Combine("c1", "projects", "p", "s.jsonl"),
            ClaudeLine("msg_1", "req_1", "2026-08-21T13:00:00Z", input: 10, cacheCreate: 0, cacheRead: 0, output: 5, thinking: 0, ephemeral1h: 0));
        WriteFile(Path.Combine("cx", "sessions", "rollout-1.jsonl"),
            CodexTurnContext("gpt-5.6-sol"),
            CodexTokenCount("2026-08-21T13:00:00Z", input: 20, cached: 0, output: 6));

        var result = await Collect(
            Claude("claude-1", Path.Combine(_root, "c1")),
            Codex("codex-9", Path.Combine(_root, "cx")));

        Assert.Equal(2, result.Samples.Count);
        Assert.Single(result.Samples, sample => sample.AccountId == "claude-1" && sample.Provider == UsageProviderKind.Claude);
        Assert.Single(result.Samples, sample => sample.AccountId == UsageAccounts.MergedCodexId && sample.Provider == UsageProviderKind.Codex);
        Assert.Equal(["claude-1", UsageAccounts.MergedCodexId], result.Accounts.Select(account => account.AccountId).Order());
    }

    // -- Per-file cache ----------------------------------------------------

    [Fact]
    public void Cached_parses_round_trip_and_are_invalidated_by_a_change()
    {
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        var path = WriteFile("a.jsonl", ClaudeLine("msg_1", "req_1", "2026-08-21T13:28:42Z"));
        var file = new FileInfo(path);

        var parsed = UsageLogParser.ParseClaudeFile(path);
        Assert.Null(cache.TryRead(file, UsageProviderKind.Claude));

        cache.Write(file, UsageProviderKind.Claude, parsed);
        var restored = cache.TryRead(file, UsageProviderKind.Claude);

        Assert.NotNull(restored);
        Assert.Equal(parsed.Entries, restored.Entries);
        Assert.Equal(parsed.SkippedLines, restored.SkippedLines);

        File.AppendAllText(path, ClaudeLine("msg_2", "req_2", "2026-08-21T13:29:00Z") + Environment.NewLine);
        Assert.Null(cache.TryRead(new FileInfo(path), UsageProviderKind.Claude));
    }

    [Fact]
    public void A_disabled_cache_stores_nothing()
    {
        var path = WriteFile("a.jsonl", ClaudeLine("msg_1", "req_1", "2026-08-21T13:28:42Z"));
        var file = new FileInfo(path);

        UsageFileCache.Disabled.Write(file, UsageProviderKind.Claude, UsageLogParser.ParseClaudeFile(path));

        Assert.Null(UsageFileCache.Disabled.TryRead(file, UsageProviderKind.Claude));
        Assert.Null(UsageFileCache.Disabled.Directory);
    }

    [Fact]
    public async Task A_second_scan_reads_the_cache_and_returns_the_same_numbers()
    {
        WriteFile(Path.Combine("c1", "projects", "p", "s.jsonl"),
            ClaudeLine("msg_1", "req_1", "2026-08-21T13:00:00Z", input: 10, cacheCreate: 0, cacheRead: 0, output: 5, thinking: 0, ephemeral1h: 0));
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        var collector = new UsageLogCollector(() => [Claude("claude-1", Path.Combine(_root, "c1"))], cache);

        var cold = await collector.CollectAsync();
        var warm = await collector.CollectAsync();

        Assert.Equal(1, cold.Diagnostics.FilesParsed);
        Assert.Equal(0, cold.Diagnostics.FilesFromCache);
        Assert.Equal(0, warm.Diagnostics.FilesParsed);
        Assert.Equal(1, warm.Diagnostics.FilesFromCache);
        Assert.Equal(
            cold.Samples.Sum(sample => sample.Tokens.ProcessedTokens),
            warm.Samples.Sum(sample => sample.Tokens.ProcessedTokens));
        Assert.Equal(cold.Samples[0], warm.Samples[0]);
    }

    // -- End to end --------------------------------------------------------

    [Fact]
    public async Task A_scan_of_both_providers_produces_a_priced_report()
    {
        WriteFile(Path.Combine("c1", "projects", "p", "s.jsonl"),
            ClaudeLine("msg_1", "req_1", "2026-08-21T13:00:00Z",
                model: "claude-opus-5", input: 1_000_000, cacheCreate: 0, cacheRead: 1_000_000, output: 1_000_000, thinking: 0, ephemeral1h: 0));
        WriteFile(Path.Combine("cx", "sessions", "rollout-1.jsonl"),
            CodexTurnContext("gpt-5.6-sol"),
            CodexTokenCount("2026-08-21T13:00:00Z", input: 1_000_000, cached: 500_000, cacheWrite: 100_000, output: 200_000));
        // Auto review has no published rate, so it is counted but never costed.
        WriteFile(Path.Combine("cx", "sessions", "rollout-2.jsonl"),
            CodexTurnContext("codex-auto-review", "2026-08-21T14:00:00Z"),
            CodexTokenCount("2026-08-21T14:00:00Z", input: 1_000, cached: 0, output: 100));

        var service = new UsageAnalyticsService(
            new UsageLogCollector(
                () => [Claude("claude-1", Path.Combine(_root, "c1")), Codex("codex-1", Path.Combine(_root, "cx"))],
                UsageFileCache.Disabled),
            ModelPricingTable.Default)
        {
            TimeZone = TimeZoneInfo.Utc
        };

        var report = await service.GetReportAsync(UsageDateRange.All);

        // Claude opus 5: $5 input + $0.50 cache read + $25 output = $30.50.
        // Codex sol: 0.5 MTok uncached at $4 = $2.00, 0.5 MTok cache read at
        // $0.40 = $0.20, 0.1 MTok cache write at $5 = $0.50, 0.2 MTok output at
        // $20 = $4.00, so $6.70. Auto review adds nothing but its tokens.
        Assert.Equal(37.2m, report.Totals.CostUsd);
        // Claude saved 1 MTok * $4.50; Codex saved 0.5 MTok * ($4 - $0.40).
        Assert.Equal(6.3m, report.Totals.CacheSavingsUsd);
        Assert.Equal(4_301_100, report.Totals.Tokens.ProcessedTokens);
        Assert.Equal(1_100, report.Totals.UnpricedTokens);
        Assert.Equal(["codex-auto-review"], report.UnpricedModels);
        Assert.Equal(2, report.ByProvider.Count);
        Assert.Equal(["claude-1", UsageAccounts.MergedCodexId], (await service.GetAccountsAsync()).Select(a => a.AccountId).Order());

        var claudeOnly = await service.GetReportAsync(UsageDateRange.All, ["claude-1"]);
        Assert.Equal(3_000_000, claudeOnly.Totals.Tokens.ProcessedTokens);
        Assert.Equal(0, claudeOnly.Totals.UnpricedTokens);
    }

    private Task<UsageScanResult> Collect(params MonitoredAccountSettings[] accounts) =>
        new UsageLogCollector(() => accounts, UsageFileCache.Disabled).CollectAsync();

    private static MonitoredAccountSettings Claude(string id, string configDir) => new()
    {
        Id = id,
        Type = MonitoredAccountSettings.ClaudeType,
        DisplayName = id,
        ConfigDir = configDir
    };

    private static MonitoredAccountSettings Codex(string id, string configDir) => new()
    {
        Id = id,
        Type = MonitoredAccountSettings.CodexType,
        DisplayName = id,
        ConfigDir = configDir
    };
}
