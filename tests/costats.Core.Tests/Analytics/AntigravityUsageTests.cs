using System.Globalization;
using System.Text;
using costats.Application.Settings;
using costats.Core.Analytics;
using costats.Core.Remote;
using costats.Infrastructure.Analytics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace costats.Core.Tests.Analytics;

/// <summary>
/// Antigravity usage, from a conversation database record to the report:
/// the record parser, the database reader, the scan, the parse cache, the
/// usage history and the remote payload. Records are built here field by
/// field in the layout real Antigravity records use, with stand-in values.
/// </summary>
public sealed class AntigravityUsageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "costats-antigravity-usage-tests", Guid.NewGuid().ToString("n"));

    private static readonly DateTimeOffset CallTime = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    public AntigravityUsageTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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

    // -- Record parser -------------------------------------------------------

    [Fact]
    public void A_record_in_the_real_layout_maps_to_its_token_counts()
    {
        var record = Record("req-1", uncached: 2934, cacheRead: 18840, output: 140, thinking: 34, response: 106);

        Assert.True(AntigravityUsageParser.TryParseRecord(record, out var entry));

        Assert.Equal(CallTime, entry.Timestamp);
        Assert.Equal("gemini-3.8-flash-medium", entry.Model);
        Assert.Equal(2934, entry.Tokens.UncachedInputTokens);
        Assert.Equal(18840, entry.Tokens.CacheReadInputTokens);
        Assert.Equal(140, entry.Tokens.OutputTokens);
        Assert.Equal(34, entry.Tokens.ReasoningOutputTokens);
        Assert.Equal(0, entry.Tokens.CacheWriteInputTokens);
        Assert.Equal(UsageLogParser.StableKey("antigravity", "req-1"), entry.DedupKey);
        Assert.NotEqual(0, entry.DedupKey);
    }

    [Fact]
    public void Unknown_fields_of_every_wire_type_are_stepped_over()
    {
        var record = Record("req-1", extraGeneration:
        [
            Proto.Fixed64(30, 0x0102030405060708),
            Proto.Fixed32(31, 0x01020304),
            Proto.Varint(32, ulong.MaxValue),
            Proto.Message(33, Proto.Fixed64(1, 7), Proto.String(2, "nested text"))
        ],
        extraUsage: [Proto.Fixed32(40, 9), Proto.Fixed64(41, 9), Proto.String(42, "x")]);

        Assert.True(AntigravityUsageParser.TryParseRecord(record, out var entry));
        Assert.Equal(1000, entry.Tokens.UncachedInputTokens);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("model")]
    [InlineData("time")]
    [InlineData("usage")]
    public void A_record_without_its_identity_model_time_or_usage_is_rejected(string missing)
    {
        var record = Record("req-1", omit: missing);

        Assert.False(AntigravityUsageParser.TryParseRecord(record, out _));
    }

    [Fact]
    public void A_request_id_written_value_first_is_still_read()
    {
        var reversed = Proto.Message(20, Proto.String(2, "req-reversed"), Proto.String(1, "request_id"));
        var record = Record("unused", omit: "request", extraGeneration: [reversed]);

        Assert.True(AntigravityUsageParser.TryParseRecord(record, out var entry));
        Assert.Equal(UsageLogParser.StableKey("antigravity", "req-reversed"), entry.DedupKey);
    }

    [Fact]
    public void A_large_number_in_an_unknown_usage_field_is_not_a_token_count()
    {
        var record = Record("req-1", extraUsage: [Proto.Varint(11, 5_000_000_000)]);

        Assert.True(AntigravityUsageParser.TryParseRecord(record, out var entry));
        Assert.Equal(1000, entry.Tokens.UncachedInputTokens);
    }

    [Fact]
    public void A_varint_longer_than_64_bits_rejects_the_record()
    {
        // Ten bytes whose last one carries more than the 64th bit would
        // otherwise wrap to a small, plausible token count.
        byte[] overflow = [(2 << 3) | 0, 0x8A, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x02];
        var record = Record("req-1", extraUsage: [overflow]);

        Assert.False(AntigravityUsageParser.TryParseRecord(record, out _));
    }

    [Fact]
    public void A_token_count_out_of_range_rejects_the_record()
    {
        var record = Record("req-1", uncached: AntigravityUsageParser.MaxTokensPerField + 1);

        Assert.False(AntigravityUsageParser.TryParseRecord(record, out _));
    }

    [Fact]
    public void A_group_wire_type_rejects_the_record()
    {
        var record = Record("req-1", extraGeneration: [[(byte)((14 << 3) | 3)]]);

        Assert.False(AntigravityUsageParser.TryParseRecord(record, out _));
    }

    [Fact]
    public void Truncated_and_random_bytes_never_throw()
    {
        var record = Record("req-1");
        for (var length = 0; length < record.Length; length++)
        {
            _ = AntigravityUsageParser.TryParseRecord(record.AsSpan(0, length), out _);
        }

        var random = new Random(1234);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(0, 300)];
            random.NextBytes(bytes);
            _ = AntigravityUsageParser.TryParseRecord(bytes, out _);
        }

        // A length that points past the end of the record.
        Assert.False(AntigravityUsageParser.TryParseRecord([0x0A, 0xFF, 0xFF, 0xFF, 0x0F, 0x01], out _));
    }

    // -- Database reader -----------------------------------------------------

    [Fact]
    public void A_database_is_read_and_bad_records_are_counted()
    {
        var path = Database("a.db", Record("req-1"), Record("req-2"), [0x0A, 0x02, 0x01]);

        var parsed = AntigravityUsageParser.ParseFile(path);

        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal(1, parsed.SkippedLines);
    }

    [Fact]
    public void A_database_without_model_calls_yet_is_empty()
    {
        var path = Path.Combine(_root, "empty.db");
        using (var connection = Open(path))
        {
            Execute(connection, "create table steps (idx integer primary key)");
        }

        Assert.Same(ParsedUsageFile.Empty, AntigravityUsageParser.ParseFile(path));
    }

    [Fact]
    public void An_oversized_record_is_skipped_without_being_decoded()
    {
        var huge = new byte[AntigravityUsageParser.MaxRecordBytes + 1];
        var path = Database("big.db", Record("req-1"), huge);

        var parsed = AntigravityUsageParser.ParseFile(path);

        Assert.Single(parsed.Entries);
        Assert.Equal(1, parsed.SkippedLines);
    }

    [Fact]
    public void A_database_with_too_many_records_fails_instead_of_being_cut_short()
    {
        var path = Path.Combine(_root, "many.db");
        using (var connection = Open(path))
        {
            CreateTable(connection);
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "insert into gen_metadata (idx, data, size) values ($idx, x'00', 1)";
            var idx = insert.Parameters.Add("$idx", SqliteType.Integer);
            for (var i = 0; i <= AntigravityUsageParser.MaxRecords; i++)
            {
                idx.Value = i;
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Assert.Throws<InvalidDataException>(() => AntigravityUsageParser.ParseFile(path));
    }

    [Fact]
    public void A_file_that_is_not_a_database_fails()
    {
        var path = Path.Combine(_root, "junk.db");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('x', 4096)));

        Assert.Throws<SqliteException>(() => AntigravityUsageParser.ParseFile(path));
    }

    // -- Scan ----------------------------------------------------------------

    [Fact]
    public async Task Both_conversation_folders_are_scanned_only_while_antigravity_is_on()
    {
        var home = Path.Combine(_root, "home");
        Database(CliFolder(home), "a.db", Record("req-1"), Record("req-shared"));
        Database(T3Folder(home, "profile-hash"), "b.db", Record("req-2"), Record("req-shared"));
        var enabled = false;
        var collector = new UsageLogCollector(
            () => [],
            UsageFileCache.Disabled,
            antigravityEnabled: () => enabled,
            antigravityLocations: new AntigravityUsageLocations(home));

        Assert.Empty((await collector.CollectAsync()).Samples);

        enabled = true;
        var scan = await collector.CollectAsync();

        Assert.Equal(3, scan.Samples.Count);
        Assert.All(scan.Samples, sample =>
        {
            Assert.Equal(UsageProviderKind.Antigravity, sample.Provider);
            Assert.Equal(UsageAccounts.MergedAntigravityId, sample.AccountId);
        });
        Assert.Equal(1, scan.Diagnostics.DuplicatesDropped);
        var account = Assert.Single(scan.Accounts);
        Assert.Equal(UsageAccounts.MergedAntigravityDisplayName, account.DisplayName);
        Assert.True(scan.IsComplete);
    }

    [Fact]
    public async Task Only_database_files_are_read_from_a_conversation_folder()
    {
        var home = Path.Combine(_root, "home");
        var folder = CliFolder(home);
        Database(folder, "a.db", Record("req-1"));
        File.WriteAllText(Path.Combine(folder, "a.meta"), "{}");
        File.WriteAllText(Path.Combine(folder, "notes.dbx"), "not a database");

        var scan = await AntigravityCollector(home).CollectAsync();

        Assert.Single(scan.Samples);
        Assert.Equal(0, scan.Diagnostics.FilesFailed);
    }

    [Fact]
    public async Task A_conversation_with_too_many_calls_fails_alone_and_the_scan_goes_on()
    {
        var home = Path.Combine(_root, "home");
        var folder = CliFolder(home);
        Database(folder, "ok.db", Record("req-1"));
        using (var connection = Open(Path.Combine(folder, "huge.db")))
        {
            CreateTable(connection);
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "insert into gen_metadata (idx, data, size) values ($idx, x'00', 1)";
            var idx = insert.Parameters.Add("$idx", SqliteType.Integer);
            for (var i = 0; i <= AntigravityUsageParser.MaxRecords; i++)
            {
                idx.Value = i;
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var scan = await AntigravityCollector(home).CollectAsync();

        Assert.Single(scan.Samples);
        Assert.Equal(1, scan.Diagnostics.FilesFailed);
        Assert.False(scan.IsCompleteFor(UsageProviderKind.Antigravity));
    }

    [Fact]
    public async Task A_broken_antigravity_database_leaves_claude_complete()
    {
        var home = Path.Combine(_root, "home");
        var folder = CliFolder(home);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "broken.db"), Encoding.ASCII.GetBytes(new string('x', 4096)));

        var scan = await AntigravityCollector(home).CollectAsync();

        Assert.Equal(1, scan.Diagnostics.FilesFailed);
        Assert.False(scan.IsComplete);
        Assert.False(scan.IsCompleteFor(UsageProviderKind.Antigravity));
        Assert.True(scan.IsCompleteFor(UsageProviderKind.Claude));
    }

    // -- Parse cache ---------------------------------------------------------

    [Fact]
    public async Task Calls_committed_to_the_wal_are_seen_through_the_cache()
    {
        var home = Path.Combine(_root, "home");
        var folder = CliFolder(home);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "live.db");
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        var collector = AntigravityCollector(home, cache);

        // A writer that keeps the database open, as the agent does: its
        // commits stay in the WAL and the database file is not touched.
        using var writer = Open(path);
        Execute(writer, "pragma journal_mode = wal");
        Execute(writer, "pragma wal_autocheckpoint = 0");
        CreateTable(writer);
        Insert(writer, 0, Record("req-1"));

        Assert.Single((await collector.CollectAsync()).Samples);
        var unchanged = await collector.CollectAsync();
        Assert.Single(unchanged.Samples);
        Assert.Equal(1, unchanged.Diagnostics.FilesFromCache);

        var before = UsageFileSignature.Capture(new FileInfo(path), UsageProviderKind.Antigravity);
        Insert(writer, 1, Record("req-2"));
        Assert.NotEqual(before, UsageFileSignature.Capture(new FileInfo(path), UsageProviderKind.Antigravity));

        var after = await collector.CollectAsync();
        Assert.Equal(2, after.Samples.Count);
        Assert.Equal(0, after.Diagnostics.FilesFromCache);
    }

    [Fact]
    public async Task Cached_antigravity_usage_is_never_recovered_as_claude_history()
    {
        var home = Path.Combine(_root, "home");
        var path = Database(CliFolder(home), "gone.db", Record("req-1"));
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        var collector = AntigravityCollector(home, cache);
        await collector.CollectAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(path);

        var scan = await collector.CollectAsync();
        var recovered = collector.RecoverOrphanedClaudeEntries(scan, DateTimeOffset.UtcNow, out var complete);

        Assert.Empty(recovered);
        Assert.True(complete);
        Assert.Equal(1, cache.Inspect().FileCount);
        cache.Clear();
        Assert.Equal(0, cache.Inspect().FileCount);
    }

    // -- Usage history -------------------------------------------------------

    [Fact]
    public async Task Antigravity_usage_is_saved_under_its_own_key_and_survives_its_database()
    {
        var home = Path.Combine(_root, "home");
        var path = Database(CliFolder(home), "a.db", Record("req-1"), Record("req-2"));
        var service = Service(home, () => []);

        await service.SyncHistoryAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(path);
        var report = await service.GetReportAsync(UsageDateRange.All);

        Assert.Equal(2, report.Totals.RequestCount);
        var saved = new UsageHistoryStore(Path.Combine(_root, "history")).Read();
        Assert.All(saved.Buckets.Keys, key =>
        {
            Assert.Equal(UsageHistory.AntigravityAccountKey, key.AccountKey);
            Assert.Equal(UsageProviderKind.Antigravity, key.Provider);
        });
        var account = Assert.Single(await service.GetAccountsAsync());
        Assert.Equal(UsageAccounts.MergedAntigravityId, account.AccountId);
    }

    [Fact]
    public async Task Deleted_history_does_not_come_back_from_a_database_still_on_disk()
    {
        var home = Path.Combine(_root, "home");
        Database(CliFolder(home), "a.db", Record("req-1"));
        var service = Service(home, () => []);
        await service.SyncHistoryAsync();

        Assert.True(await service.DeleteHistoryAsync());
        await service.SyncHistoryAsync();

        Assert.True((await service.GetReportAsync(UsageDateRange.All)).IsEmpty);
        Assert.Empty(new UsageHistoryStore(Path.Combine(_root, "history")).Read().Buckets);
    }

    [Fact]
    public async Task A_broken_antigravity_database_does_not_hold_up_the_claude_recovery()
    {
        var home = Path.Combine(_root, "home");
        var folder = CliFolder(home);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "broken.db"), Encoding.ASCII.GetBytes(new string('x', 4096)));

        var claude = new MonitoredAccountSettings
        {
            Id = "claude-1",
            Type = MonitoredAccountSettings.ClaudeType,
            DisplayName = "Work",
            ConfigDir = Path.Combine(_root, "c1")
        };
        IReadOnlyList<MonitoredAccountSettings> accounts = [claude];
        var cache = new UsageFileCache(Path.Combine(_root, "cache"));
        var log = Path.Combine(_root, "c1", "projects", "p", "old.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, ClaudeLine("1", "2026-07-01T10:00:00Z") + "\n");
        await new UsageLogCollector(() => accounts, cache).CollectAsync();
        File.Delete(log);

        var service = Service(home, () => accounts, cache);
        await service.SyncHistoryAsync();

        var store = new UsageHistoryStore(Path.Combine(_root, "history"));
        Assert.False(store.NeedsCacheRecovery());
        Assert.Equal(1, (await service.GetReportAsync(UsageDateRange.All)).Totals.RequestCount);
    }

    // -- Remote payload ------------------------------------------------------

    [Fact]
    public void The_payload_stays_version_1_without_antigravity_and_becomes_version_2_with_it()
    {
        var now = CallTime.AddHours(1);
        var samples = new List<UsageSample>
        {
            new(CallTime, UsageProviderKind.Claude, "claude-1", "claude-sonnet-4-5", new UsageTokens { UncachedInputTokens = 10, OutputTokens = 5 }),
            new(CallTime, UsageProviderKind.Antigravity, UsageAccounts.MergedAntigravityId, "gemini-3.8-flash-medium", new UsageTokens { UncachedInputTokens = 10, OutputTokens = 5 })
        };
        var source = new RemoteUsageSource(samples, ModelPricingTable.Default,
        [
            new RemoteUsageAccountSource("claude-1", UsageProviderKind.Claude, "Work"),
            new RemoteUsageAccountSource(UsageAccounts.MergedAntigravityId, UsageProviderKind.Antigravity, UsageAccounts.MergedAntigravityDisplayName)
        ]);

        var without = RemoteUsageComposer.Compose(source, "Etc/UTC", TimeZoneInfo.Utc, now).Payload!;
        var with = RemoteUsageComposer.Compose(source, "Etc/UTC", TimeZoneInfo.Utc, now, includeAntigravity: true).Payload!;

        Assert.Equal(RemoteUsagePayload.BaseVersion, without.Version);
        Assert.DoesNotContain(without.Accounts, account => account.Provider == "antigravity");
        Assert.DoesNotContain(without.Models, model => model.Provider == "antigravity");
        Assert.Contains("\"version\":1", Encoding.UTF8.GetString(without.ToUtf8Json(1)), StringComparison.Ordinal);

        Assert.Equal(RemoteUsagePayload.AntigravityVersion, with.Version);
        Assert.Contains(new RemoteUsageAccount(UsageAccounts.MergedAntigravityDisplayName, "antigravity"), with.Accounts);
        var model = Assert.Single(with.Models, model => model.Provider == "antigravity");
        Assert.Equal("gemini-3.8-flash-medium", model.Id);
        Assert.True(model.Priced);
        Assert.Contains("\"version\":2", Encoding.UTF8.GetString(with.ToUtf8Json(1)), StringComparison.Ordinal);
    }

    // -- Accounts and prices -------------------------------------------------

    [Fact]
    public void The_tray_antigravity_account_binds_to_the_merged_bucket_with_a_scope_note()
    {
        var binding = UsageAccountMap.Resolve(
            "antigravity",
            [new UsageAccountInfo(UsageAccounts.MergedAntigravityId, UsageAccounts.MergedAntigravityDisplayName, UsageProviderKind.Antigravity)]);

        Assert.NotNull(binding);
        Assert.True(binding.IsMerged);
        Assert.Equal(UsageProviderKind.Antigravity, binding.Provider);
        Assert.Equal(UsageAccountMap.MergedAntigravityScopeNote, binding.ScopeNote);
        Assert.Null(UsageAccountMap.Resolve("antigravity", [new UsageAccountInfo("claude-1", "Work", UsageProviderKind.Claude)]));
    }

    [Fact]
    public void A_gemini_thinking_level_is_priced_as_its_model_and_nothing_else_is_stripped()
    {
        var base_ = new ModelPrice { InputPerMTok = 0.75m, OutputPerMTok = 3.75m, CachedInputPerMTok = 0.075m };
        var table = new ModelPricingTable(
        [
            new KeyValuePair<string, ModelPrice>("gemini-3.8-flash", base_),
            new KeyValuePair<string, ModelPrice>("gpt-9", base_)
        ]);

        Assert.Same(base_, table.Find("gemini-3.8-flash-medium"));
        Assert.Same(base_, table.Find("gemini-3.8-flash-high"));
        Assert.Same(base_, table.Find("gemini-3.8-flash-low"));
        Assert.False(table.Find("gemini-3.8-flash-extreme").IsPriced);
        Assert.False(table.Find("gpt-9-medium").IsPriced);
    }

    [Fact]
    public void The_catalog_keeps_google_gemini_models_and_no_other_vertex_entries()
    {
        var models = new StringBuilder("{");
        for (var i = 0; i < 25; i++)
        {
            models.Append(CultureInfo.InvariantCulture, $"\"claude-test-{i}\":{{\"litellm_provider\":\"anthropic\",\"mode\":\"chat\",\"input_cost_per_token\":1e-6,\"output_cost_per_token\":2e-6}},");
        }

        models.Append("\"gemini-3.8-flash\":{\"litellm_provider\":\"vertex_ai-language-models\",\"mode\":\"chat\",\"input_cost_per_token\":7.5e-7,\"output_cost_per_token\":3.75e-6,\"cache_read_input_token_cost\":7.5e-8},");
        models.Append("\"partner-model\":{\"litellm_provider\":\"vertex_ai-language-models\",\"mode\":\"chat\",\"input_cost_per_token\":1e-6,\"output_cost_per_token\":1e-6},");
        models.Append("\"gemini/gemini-3.8-flash\":{\"litellm_provider\":\"gemini\",\"mode\":\"chat\",\"input_cost_per_token\":7.5e-7,\"output_cost_per_token\":3.75e-6}}");

        var table = LiteLlmPricingCatalog.TryParse(models.ToString())!;

        Assert.Equal(0.75m, table.Find("gemini-3.8-flash-medium").InputPerMTok);
        Assert.Equal(0.075m, table.Find("gemini-3.8-flash").CachedInputPerMTok);
        Assert.False(table.Find("partner-model").IsPriced);
        Assert.False(table.Entries.ContainsKey("gemini/gemini-3.8-flash"));
    }

    // -- Helpers -------------------------------------------------------------

    private const string ClaudeTemplate = """
        {"type":"assistant","timestamp":"@TS@","requestId":"req_@ID@","sessionId":"s1","uuid":"u1","message":{"id":"msg_@ID@","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":"end_turn","usage":{"input_tokens":10,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":5}}}
        """;

    private static string ClaudeLine(string id, string timestamp) =>
        ClaudeTemplate.Replace("@TS@", timestamp, StringComparison.Ordinal).Replace("@ID@", id, StringComparison.Ordinal);

    private UsageAnalyticsService Service(
        string home,
        Func<IReadOnlyList<MonitoredAccountSettings>> accounts,
        UsageFileCache? cache = null) =>
        new(
            new UsageLogCollector(accounts, cache ?? UsageFileCache.Disabled, antigravityEnabled: () => true, antigravityLocations: new AntigravityUsageLocations(home)),
            ModelPricingTable.Default,
            freshness: TimeSpan.Zero)
        {
            TimeZone = TimeZoneInfo.Utc,
            History = new UsageHistoryOptions(new UsageHistoryStore(Path.Combine(_root, "history")), accounts, () => 0)
            {
                AntigravityEnabled = () => true
            }
        };

    private static UsageLogCollector AntigravityCollector(string home, UsageFileCache? cache = null) =>
        new(() => [], cache ?? UsageFileCache.Disabled, antigravityEnabled: () => true, antigravityLocations: new AntigravityUsageLocations(home));

    private static string CliFolder(string home) => Path.Combine(home, ".gemini", "antigravity-cli", "conversations");

    private static string T3Folder(string home, string profile) =>
        Path.Combine(home, ".t3", "userdata", "providers", "antigravity", profile, "antigravity-acp", "conversations");

    private string Database(string name, params byte[][] records) => Database(_root, name, records);

    private static string Database(string folder, string name, params byte[][] records)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        using var connection = Open(path);
        CreateTable(connection);
        for (var i = 0; i < records.Length; i++)
        {
            Insert(connection, i, records[i]);
        }

        return path;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void CreateTable(SqliteConnection connection) =>
        Execute(connection, "create table gen_metadata (idx integer, data blob, size integer not null default 0, primary key (idx))");

    private static void Insert(SqliteConnection connection, int idx, byte[] data)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "insert into gen_metadata (idx, data, size) values ($idx, $data, $size)";
        command.Parameters.AddWithValue("$idx", idx);
        command.Parameters.AddWithValue("$data", data);
        command.Parameters.AddWithValue("$size", data.Length);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A <c>gen_metadata</c> record in the layout real ones have: the
    /// generation message in field 1, with usage in 4, the time in 9.4, key
    /// and value pairs in 20, the model in 21, and the unrelated fields real
    /// records carry around them.
    /// </summary>
    private static byte[] Record(
        string requestId,
        long uncached = 1000,
        long cacheRead = 5000,
        long output = 300,
        long thinking = 200,
        long response = 100,
        string? omit = null,
        byte[][]? extraGeneration = null,
        byte[][]? extraUsage = null)
    {
        var usage = new List<byte[]>
        {
            Proto.Varint(2, (ulong)uncached),
            Proto.Varint(3, (ulong)output),
            Proto.Varint(9, (ulong)thinking),
            Proto.Varint(10, (ulong)response)
        };
        if (cacheRead > 0)
        {
            usage.Add(Proto.Varint(5, (ulong)cacheRead));
        }

        usage.AddRange(extraUsage ?? []);

        var generation = new List<byte[]>
        {
            Proto.Varint(3, 326),
            Proto.Message(7, Proto.String(1, "auto"))
        };
        if (omit != "usage")
        {
            generation.Add(Proto.Message(4, [.. usage]));
        }

        if (omit != "time")
        {
            generation.Add(Proto.Message(9,
                Proto.Varint(2, ulong.MaxValue),
                Proto.Message(4, Proto.Varint(1, (ulong)CallTime.ToUnixTimeSeconds()), Proto.Varint(2, 345307500)),
                Proto.String(8, string.Empty),
                Proto.Message(10, Proto.Varint(1, 3347), Proto.Varint(4, 256000))));
        }

        generation.Add(Proto.Message(17, Proto.Message(2, Proto.Varint(2, (ulong)uncached), Proto.Varint(3, (ulong)output))));
        generation.Add(Proto.String(19, "gemini-3.8-flash-n"));
        generation.Add(Pair("last_step_index", "0"));
        generation.Add(Pair("model_enum", "MODEL_GOOGLE_GEMINI_INTERNAL_BYOM"));
        generation.Add(Pair("trajectory_id", "trajectory-1"));
        if (omit != "request")
        {
            generation.Add(Pair("request_id", requestId));
        }

        generation.Add(Pair("used_claude", "false"));
        if (omit != "model")
        {
            generation.Add(Proto.String(21, "gemini-3.8-flash-medium"));
        }

        generation.AddRange(extraGeneration ?? []);

        return Proto.Concat(
            Proto.Message(1, [.. generation]),
            Proto.Bytes(2, [0x01, 0x02]),
            Proto.String(4, "trajectory-1"),
            Proto.Varint(10, 1));

        static byte[] Pair(string key, string value) => Proto.Message(20, Proto.String(1, key), Proto.String(2, value));
    }

    /// <summary>A protobuf writer, just enough to build test records.</summary>
    private static class Proto
    {
        public static byte[] Varint(int field, ulong value) => Concat(Tag(field, 0), RawVarint(value));

        public static byte[] Fixed64(int field, ulong value) => Concat(Tag(field, 1), BitConverter.GetBytes(value));

        public static byte[] Fixed32(int field, uint value) => Concat(Tag(field, 5), BitConverter.GetBytes(value));

        public static byte[] Bytes(int field, byte[] value) => Concat(Tag(field, 2), RawVarint((ulong)value.Length), value);

        public static byte[] String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public static byte[] Message(int field, params byte[][] parts) => Bytes(field, Concat(parts));

        public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

        private static byte[] Tag(int field, int wireType) => RawVarint(((ulong)field << 3) | (uint)wireType);

        private static byte[] RawVarint(ulong value)
        {
            var bytes = new List<byte>();
            do
            {
                var next = (byte)(value & 0x7F);
                value >>= 7;
                bytes.Add(value == 0 ? next : (byte)(next | 0x80));
            }
            while (value != 0);

            return [.. bytes];
        }
    }
}
